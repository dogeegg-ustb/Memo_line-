using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using BehaviorRecognizer.Abstractions.Stroke;
using BehaviorRecognizer.Storage.Strokebin;

namespace StrokeReplay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        CspPlacement.PrepareDpiAwareness();
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.Run(new ReplayWindow(args.FirstOrDefault()));
    }

    private static void Inspect(string path)
    {
        var session = ReadSession(path);
        var strokes = session.Segments.SelectMany(s => s.Strokes)
            .Where(s => s.Points.Count > 0)
            .ToArray();
        var points = strokes.SelectMany(s => s.Points).ToArray();

        Console.WriteLine($"文件: {Path.GetFullPath(path)}");
        Console.WriteLine($"格式: STRO v{session.Header.Version}, 编码={session.Header.Encoding}");
        Console.WriteLine($"设备: {session.Header.Device.Name} ({session.Header.Device.Id})");
        Console.WriteLine($"分段: {session.Segments.Count}, 笔划: {strokes.Length}, 点数: {points.Length}");

        if (points.Length == 0)
        {
            Console.WriteLine("没有可回放的笔点。");
            return;
        }

        var first = points.Min(p => p.TimestampMs);
        var last = points.Max(p => p.TimestampMs);
        Console.WriteLine(FormattableString.Invariant(
            $"X: {points.Min(p => p.X):0.###}..{points.Max(p => p.X):0.###}, Y: {points.Min(p => p.Y):0.###}..{points.Max(p => p.Y):0.###}"));
        Console.WriteLine(FormattableString.Invariant(
            $"压力: {points.Min(p => p.Pressure):0.###}..{points.Max(p => p.Pressure):0.###}, 倾斜 X: {points.Min(p => p.TiltX):0.###}..{points.Max(p => p.TiltX):0.###}, 倾斜 Y: {points.Min(p => p.TiltY):0.###}..{points.Max(p => p.TiltY):0.###}"));
        Console.WriteLine($"采样跨度: {last - first} ms");
    }

    internal static RecordingSession ReadSession(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? StrokeJsonReader.Read(path)
            : StrokeBinaryReader.Read(path);

    internal static async Task<bool> PlaceAndReplayAsync(
        string filePath,
        double pressureMax,
        double speed,
        Action<string> setStatus,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("定位和 Windows 笔注入只能在 Windows 上运行。");

        var session = ReadSession(filePath);
        var strokes = session.Segments.SelectMany(s => s.Strokes).Where(s => s.Points.Count > 0).ToArray();
        var contactPoints = strokes.SelectMany(s => s.Points).Where(p => p.InContact).ToArray();
        if (contactPoints.Length == 0)
            throw new InvalidDataException("JSON 没有处于接触状态的笔点。");
        if (contactPoints.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            throw new InvalidDataException("JSON 有非有限的笔坐标。");

        double minX = contactPoints.Min(p => p.X), maxX = contactPoints.Max(p => p.X);
        double minY = contactPoints.Min(p => p.Y), maxY = contactPoints.Max(p => p.Y);
        setStatus("正在冻结 CSP 画面…拖动绿色框，按 Enter 确认，按 Esc 取消。");

        using var placement = new CspPlacement(minX, minY, maxX, maxY);
        if (placement.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return false;

        var target = placement.SelectedBounds;
        var options = new ReplayOptions(filePath, new SourceRect(minX, minY,
            Math.Max(maxX - minX, 1), Math.Max(maxY - minY, 1)),
            new TargetRect(target.Left, target.Top, target.Width, target.Height), pressureMax, speed, true);
        var frames = BuildFrames(strokes, options);

        // The snapshot overlay is gone before pen events are sent to CSP.
        if (!CspPlacement.IsTargetWindowAvailable(placement.TargetWindow))
            throw new InvalidOperationException("CSP 窗口已关闭；没有注入事件。");
        CspPlacement.ActivateTarget(placement.TargetWindow);
        setStatus($"正在向 CSP 重放 {strokes.Length} 条笔划、{contactPoints.Length} 个接触点…");
        await ReplayAsync(frames, speed, cancellationToken);
        return true;
    }

    private static List<ReplayFrame> BuildFrames(Stroke[] strokes, ReplayOptions options)
    {
        var frames = new List<ReplayFrame>();

        foreach (var stroke in strokes)
        {
            var isDown = false;
            MappedPoint lastPoint = default;
            ulong lastTimestamp = 0;

            foreach (var point in stroke.Points)
            {
                var mapped = MapPoint(point, options);
                lastPoint = mapped;
                lastTimestamp = point.TimestampMs;

                if (!point.InContact)
                {
                    if (isDown)
                    {
                        frames.Add(new ReplayFrame(
                            Math.Max(point.TimestampMs, stroke.EndTimestampMs),
                            PointerTransition.Up,
                            mapped));
                        isDown = false;
                    }
                    continue;
                }

                var transition = isDown ? PointerTransition.Update : PointerTransition.Down;
                frames.Add(new ReplayFrame(point.TimestampMs, transition, mapped));
                isDown = true;
            }

            if (isDown)
            {
                var upAt = Math.Max(lastTimestamp, stroke.EndTimestampMs);
                frames.Add(new ReplayFrame(upAt, PointerTransition.Up, lastPoint with { Pressure = 0 }));
            }
        }

        if (frames.Count == 0)
            throw new InvalidDataException("文件中没有处于接触状态的笔点。");

        return frames.OrderBy(frame => frame.TimestampMs).ToList();
    }

    private static MappedPoint MapPoint(SamplePoint point, ReplayOptions options)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            !double.IsFinite(point.Pressure) || !double.IsFinite(point.TiltX) || !double.IsFinite(point.TiltY))
            throw new InvalidDataException("文件包含非有限的坐标、压力或倾斜值。");

        var nx = Math.Clamp((point.X - options.Source.X) / options.Source.Width, 0, 1);
        var ny = Math.Clamp((point.Y - options.Source.Y) / options.Source.Height, 0, 1);
        var x = options.Target.X + (int)Math.Round(nx * (options.Target.Width - 1), MidpointRounding.AwayFromZero);
        var y = options.Target.Y + (int)Math.Round(ny * (options.Target.Height - 1), MidpointRounding.AwayFromZero);
        var pressure = (uint)Math.Clamp(
            Math.Round(point.Pressure / options.PressureMax * 1024, MidpointRounding.AwayFromZero), 0, 1024);
        var tiltX = (int)Math.Clamp(Math.Round(point.TiltX, MidpointRounding.AwayFromZero), -90, 90);
        var tiltY = (int)Math.Clamp(Math.Round(point.TiltY, MidpointRounding.AwayFromZero), -90, 90);
        return new MappedPoint(x, y, pressure, tiltX, tiltY);
    }

    private static void PrintReplaySummary(
        RecordingSession session,
        Stroke[] strokes,
        int pointCount,
        List<ReplayFrame> frames,
        ReplayOptions options)
    {
        var points = strokes.SelectMany(s => s.Points).ToArray();
        var first = frames.Min(f => f.TimestampMs);
        var last = frames.Max(f => f.TimestampMs);
        var duration = (last - first) / 1000d / options.Speed;

        Console.WriteLine($"设备: {session.Header.Device.Name}; 笔划: {strokes.Length}; 采样点: {pointCount}; 注入帧: {frames.Count}");
        Console.WriteLine(FormattableString.Invariant(
            $"源坐标范围: X {options.Source.X:0.###}..{options.Source.X + options.Source.Width:0.###}, Y {options.Source.Y:0.###}..{options.Source.Y + options.Source.Height:0.###}"));
        Console.WriteLine($"目标屏幕矩形: {options.Target.X},{options.Target.Y},{options.Target.Width},{options.Target.Height}");
        Console.WriteLine(FormattableString.Invariant(
            $"压力上限: {options.PressureMax:0.###}; 速度: {options.Speed:0.###}x; 时长约 {duration:0.###} 秒"));

        var unsupportedButtons = points.Count(p => p.Buttons != 0);
        if (unsupportedButtons > 0)
            Console.WriteLine($"注意: {unsupportedButtons} 个采样点带有侧键状态；当前原型只回放笔尖、压感和倾斜，侧键会忽略。");

        var pressureClamped = points.Count(p => p.Pressure > options.PressureMax);
        if (pressureClamped > 0)
            Console.WriteLine($"注意: {pressureClamped} 个点的原始压力超过 --pressure-max，会被钳制到最大压感。");

        var sourceClamped = points.Count(p =>
            p.X < options.Source.X || p.X > options.Source.X + options.Source.Width ||
            p.Y < options.Source.Y || p.Y > options.Source.Y + options.Source.Height);
        if (sourceClamped > 0)
            Console.WriteLine($"注意: {sourceClamped} 个点超出 --source 范围，映射时会被钳制到目标矩形边缘。");

        Console.WriteLine("前 5 个坐标/压力转换结果:");
        foreach (var point in points.Take(5))
        {
            var mapped = MapPoint(point, options);
            Console.WriteLine(FormattableString.Invariant(
                $"  ({point.X:0.###},{point.Y:0.###}) p={point.Pressure:0.###} -> ({mapped.X},{mapped.Y}) p={mapped.Pressure}/1024 tilt=({mapped.TiltX},{mapped.TiltY})"));
        }
    }

    private static async Task ReplayAsync(List<ReplayFrame> frames, double speed, CancellationToken cancellationToken)
    {
        using var injector = new SyntheticPenInjector();
        using var timer = new HighResolutionWaitableTimer();
        var clock = Stopwatch.StartNew();
        var origin = frames.Min(f => f.TimestampMs);
        var lastPoint = frames[0].Point;
        var pointerIsDown = false;

        try
        {
            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dueMs = (frame.TimestampMs - origin) / speed;
                await timer.WaitUntilAsync(clock, dueMs, cancellationToken);
                injector.Send(frame.Point, frame.Transition);
                lastPoint = frame.Point;
                pointerIsDown = frame.Transition != PointerTransition.Up;
            }
        }
        finally
        {
            if (pointerIsDown)
                injector.Send(lastPoint with { Pressure = 0 }, PointerTransition.Up);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            StrokeReplay — 将笔迹 JSON 或 STRO v1 .strokebin 回放为 Windows 合成笔事件

            用法:
              StrokeReplay inspect <文件.json|文件.strokebin>
              StrokeReplay place <文件.json> [--pressure-max N] [--speed N] [--inject]
              StrokeReplay replay <文件.strokebin> --source x,y,w,h --target x,y,w,h --pressure-max N [--speed N] [--inject]

            参数:
              --source       源数位板坐标矩形，来自 OTD 设备坐标范围，不是本次笔迹的包围盒
              --target       目标屏幕像素矩形，例如画布区域左上角和宽高
              --pressure-max 原始压力最大值；由该设备的 OTD 配置决定
              --speed        回放速度倍率，默认 1
              --inject       实际调用 Windows 笔注入 API；不传时只做转换预览

            place 会冻结 CSP 同一 UI 线程窗口的画面，按接触点包围盒比例生成可拖动矩形。
            在冻结画面上拖动矩形，按 Enter 确认、Esc 取消。确认后撤下画面，使用 PT_PEN 重放。
            JSON 不包含数位板完整量程；place 将笔迹包围盒映射到所选矩形，不需要 --source。

            示例:
              dotnet run --project recognizer/behavior_recognizer/tools/StrokeReplay -- inspect stroke/20260714_081036.strokebin
              dotnet run --project recognizer/behavior_recognizer/tools/StrokeReplay -- replay stroke/20260714_081038.strokebin --source 0,0,32767,32767 --target 200,100,1600,900 --pressure-max 16383

            注入前先用 inspect 检查文件，并确认 source、target 和压力上限。目标窗口由屏幕坐标命中，不会自动选择绘画软件。
            """);
    }

    private readonly record struct MappedPoint(int X, int Y, uint Pressure, int TiltX, int TiltY);

    private readonly record struct ReplayFrame(ulong TimestampMs, PointerTransition Transition, MappedPoint Point);

    private enum PointerTransition
    {
        Down,
        Update,
        Up
    }

    private sealed record ReplayOptions(
        string FilePath,
        SourceRect Source,
        TargetRect Target,
        double PressureMax,
        double Speed,
        bool Inject)
    {
        public static ReplayOptions Parse(string[] args)
        {
            if (args.Length == 0)
                throw new ArgumentException("replay 缺少输入文件路径。");

            var filePath = args[0];
            SourceRect? source = null;
            TargetRect? target = null;
            double? pressureMax = null;
            var speed = 1d;
            var inject = false;

            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--source":
                        source = SourceRect.Parse(NextValue(args, ref i));
                        break;
                    case "--target":
                        target = TargetRect.Parse(NextValue(args, ref i));
                        break;
                    case "--pressure-max":
                        pressureMax = ParsePositive(NextValue(args, ref i), "pressure-max");
                        break;
                    case "--speed":
                        speed = ParsePositive(NextValue(args, ref i), "speed");
                        break;
                    case "--inject":
                        inject = true;
                        break;
                    default:
                        throw new ArgumentException($"未知参数: {args[i]}");
                }
            }

            if (source is null || target is null || pressureMax is null)
                throw new ArgumentException("replay 需要 --source、--target 和 --pressure-max 参数。");

            return new ReplayOptions(filePath, source.Value, target.Value, pressureMax.Value, speed, inject);
        }

        private static string NextValue(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{args[index]} 缺少参数值。");
            return args[++index];
        }

        private static double ParsePositive(string text, string name)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value) || value <= 0)
                throw new ArgumentException($"{name} 必须是大于 0 的有限数字。");
            return value;
        }
    }

    private readonly record struct SourceRect(double X, double Y, double Width, double Height)
    {
        public static SourceRect Parse(string text)
        {
            var values = ParseRectValues(text);
            if (values[2] <= 0 || values[3] <= 0)
                throw new ArgumentException("--source 的宽高必须大于 0。");
            return new SourceRect(values[0], values[1], values[2], values[3]);
        }
    }

    private readonly record struct TargetRect(int X, int Y, int Width, int Height)
    {
        public static TargetRect Parse(string text)
        {
            var values = ParseRectValues(text);
            if (values.Any(v => !double.IsFinite(v) || Math.Truncate(v) != v) || values[2] < 1 || values[3] < 1 ||
                values[0] < int.MinValue || values[0] > int.MaxValue ||
                values[1] < int.MinValue || values[1] > int.MaxValue ||
                values[2] > int.MaxValue || values[3] > int.MaxValue)
                throw new ArgumentException("--target 必须是有效的屏幕像素矩形，宽高至少为 1。");
            return new TargetRect((int)values[0], (int)values[1], (int)values[2], (int)values[3]);
        }
    }

    private static double[] ParseRectValues(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 4 || parts.Any(p =>
                !double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)))
            throw new ArgumentException("矩形格式应为 x,y,width,height，使用英文逗号和小数点。");

        return parts.Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
    }

    private sealed class SyntheticPenInjector : IDisposable
    {
        private const uint PointerTypePen = 3;
        private const uint PointerFeedbackNone = 3;
        private const uint PenMaskPressure = 0x01;
        private const uint PenMaskTiltX = 0x04;
        private const uint PenMaskTiltY = 0x08;

        private const uint PointerFlagNew = 0x00000001;
        private const uint PointerFlagInRange = 0x00000002;
        private const uint PointerFlagInContact = 0x00000004;
        private const uint PointerFlagDown = 0x00010000;
        private const uint PointerFlagUpdate = 0x00020000;
        private const uint PointerFlagUp = 0x00040000;

        private readonly IntPtr _device;
        private readonly System.Drawing.Rectangle _virtualScreen;
        private uint _frameId;

        public static void ValidateLayout()
        {
            if (IntPtr.Size != 8 || Marshal.SizeOf<PointerInfo>() != 96 ||
                Marshal.SizeOf<PointerPenInfo>() != 120 || Marshal.SizeOf<PointerTypeInfo>() != 152)
                throw new PlatformNotSupportedException("当前进程 ABI 与 Windows x64 POINTER_TYPE_INFO 布局不匹配。");
        }

        public SyntheticPenInjector()
        {
            ValidateLayout();

            _virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (_virtualScreen.Width <= 0 || _virtualScreen.Height <= 0)
                throw new InvalidOperationException("虚拟桌面尺寸无效，无法定位合成笔输入。");

            _device = NativeMethods.CreateSyntheticPointerDevice(PointerTypePen, 1, PointerFeedbackNone);
            if (_device == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法创建 PT_PEN 合成笔设备。");
        }

        public void Send(MappedPoint point, PointerTransition transition)
        {
            if (!_virtualScreen.Contains(point.X, point.Y))
                throw new InvalidOperationException($"笔输入点 ({point.X},{point.Y}) 超出虚拟桌面范围。");

            // InjectSyntheticPointerInput takes pixels relative to the virtual
            // desktop's top-left, while the placement UI uses screen coordinates.
            var injectionPoint = new NativePoint(
                checked(point.X - _virtualScreen.Left),
                checked(point.Y - _virtualScreen.Top));
            var flags = transition switch
            {
                PointerTransition.Down => PointerFlagNew | PointerFlagInRange | PointerFlagInContact | PointerFlagDown,
                PointerTransition.Update => PointerFlagInRange | PointerFlagInContact | PointerFlagUpdate,
                PointerTransition.Up => PointerFlagInRange | PointerFlagUp,
                _ => throw new ArgumentOutOfRangeException(nameof(transition))
            };

            var info = new PointerInfo
            {
                PointerType = PointerTypePen,
                PointerId = 1,
                FrameId = ++_frameId,
                PointerFlags = flags,
                SourceDevice = IntPtr.Zero,
                HwndTarget = IntPtr.Zero,
                PtPixelLocation = injectionPoint,
                PtHimetricLocation = default,
                PtPixelLocationRaw = injectionPoint,
                PtHimetricLocationRaw = default,
                DwTime = 0,
                HistoryCount = 0,
                InputData = 0,
                DwKeyStates = 0,
                PerformanceCount = 0,
                ButtonChangeType = 0
            };

            var penInfo = new PointerPenInfo
            {
                PointerInfo = info,
                PenFlags = 0,
                PenMask = PenMaskPressure | PenMaskTiltX | PenMaskTiltY,
                Pressure = transition == PointerTransition.Up ? 0 : point.Pressure,
                Rotation = 0,
                TiltX = point.TiltX,
                TiltY = point.TiltY
            };

            var typeInfo = new PointerTypeInfo { Type = PointerTypePen, PenInfo = penInfo };
            if (!NativeMethods.InjectSyntheticPointerInput(_device, ref typeInfo, 1))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "InjectSyntheticPointerInput 调用失败。");
        }

        public void Dispose()
        {
            if (_device != IntPtr.Zero)
                NativeMethods.DestroySyntheticPointerDevice(_device);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PointerInfo
        {
            public uint PointerType;
            public uint PointerId;
            public uint FrameId;
            public uint PointerFlags;
            public IntPtr SourceDevice;
            public IntPtr HwndTarget;
            public NativePoint PtPixelLocation;
            public NativePoint PtHimetricLocation;
            public NativePoint PtPixelLocationRaw;
            public NativePoint PtHimetricLocationRaw;
            public uint DwTime;
            public uint HistoryCount;
            public int InputData;
            public uint DwKeyStates;
            public ulong PerformanceCount;
            public uint ButtonChangeType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PointerPenInfo
        {
            public PointerInfo PointerInfo;
            public uint PenFlags;
            public uint PenMask;
            public uint Pressure;
            public uint Rotation;
            public int TiltX;
            public int TiltY;
        }

        [StructLayout(LayoutKind.Explicit, Size = 152)]
        private struct PointerTypeInfo
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public PointerPenInfo PenInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NativePoint(int x, int y)
        {
            public readonly int X = x;
            public readonly int Y = y;
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
            public static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

            [DllImport("user32.dll", ExactSpelling = true)]
            public static extern void DestroySyntheticPointerDevice(IntPtr device);

            [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool InjectSyntheticPointerInput(IntPtr device, ref PointerTypeInfo pointerInfo, uint count);
        }
    }

    private sealed class HighResolutionWaitableTimer : IDisposable
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x001F0003;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;
        private readonly SafeWaitHandle _handle;

        public HighResolutionWaitableTimer()
        {
            _handle = NativeMethods.CreateWaitableTimerEx(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (_handle.IsInvalid)
            {
                _handle.Dispose();
                _handle = NativeMethods.CreateWaitableTimerEx(IntPtr.Zero, null, 0, TimerAllAccess);
            }

            if (_handle.IsInvalid)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法创建高精度等待定时器。");
        }

        public async Task WaitUntilAsync(Stopwatch clock, double dueMs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingMs = dueMs - clock.Elapsed.TotalMilliseconds;
            if (remainingMs <= 0)
                return;

            var dueTime100ns = -Math.Max(1L, (long)Math.Ceiling(remainingMs * 10_000));
            if (!NativeMethods.SetWaitableTimer(_handle, ref dueTime100ns, 0, IntPtr.Zero, IntPtr.Zero, false))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法设置回放定时器。");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var waitResult = NativeMethods.WaitForSingleObject(_handle, 50);
                if (waitResult == WaitObject0)
                    return;
                if (waitResult != WaitTimeout)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "等待回放定时器失败。");
                await Task.Yield();
            }
        }

        public void Dispose() => _handle.Dispose();

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint desiredAccess);

            [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

            [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
            public static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
        }
    }
}
