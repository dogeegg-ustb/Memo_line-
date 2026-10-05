using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StrokeReplay;

internal static class PenReplayEngine
{
    internal readonly record struct MappedPoint(int X, int Y, uint Pressure, int TiltX, int TiltY);
    internal enum PointerTransition { Down, Update, Up, UpAndLeave }
    internal interface IPenInjector : IDisposable
    {
        void Send(MappedPoint point, PointerTransition transition);
    }

    internal static async Task ReplayStrokeAsync(MemolineReplayStroke stroke, long frequency, double speed,
        Func<MemolineReplaySample, Task> ensureReady, CancellationToken token,
        ReplayOptions? options = null, Action<string>? status = null, TimeSpan followingGap = default)
        => await ReplayStrokeCoreAsync(stroke, frequency, speed, ensureReady, token, options ?? new(),
            () => new SyntheticPenInjector(), (delay, cancellation) => Task.Delay(delay, cancellation), status, followingGap);

    internal static async Task ReplayStrokeCoreAsync(MemolineReplayStroke stroke, long frequency, double speed,
        Func<MemolineReplaySample, Task> ensureReady, CancellationToken token, ReplayOptions options,
        Func<IPenInjector> createInjector, Func<TimeSpan, CancellationToken, Task> idle,
        Action<string>? status = null, TimeSpan followingGap = default)
    {
        options.Validate();
        token.ThrowIfCancellationRequested();
        using var injector = createInjector();
        using var timer = new HighResolutionWaitableTimer();
        var clock = Stopwatch.StartNew();
        bool down = false;
        long removedIdleTicks = 0;
        MappedPoint last = default;
        var up = options.Mode == ReplayMode.Discrete ? PointerTransition.UpAndLeave : PointerTransition.Up;
        async Task SeparateAsync(TimeSpan gap)
        {
            if (options.Mode != ReplayMode.Discrete || gap <= TimeSpan.Zero) return;
            gap = TimeSpan.FromSeconds(Math.Min(gap.TotalSeconds, options.StrokeGapSeconds));
            if (gap <= TimeSpan.Zero) return;
            // Keep the device alive while the target handles UP/LEAVE. No further
            // pointer or view input is generated during the bounded recorded gap.
            clock.Stop();
            status?.Invoke($"离散模式：已抬笔，按录制间隔等待 {gap.TotalMilliseconds:0.#} ms…");
            try { await idle(gap, token); }
            finally { clock.Start(); }
        }
        try
        {
            for (int sampleIndex = 0; sampleIndex < stroke.Samples.Count; sampleIndex++)
            {
                var sample = stroke.Samples[sampleIndex];
                token.ThrowIfCancellationRequested();
                // In discrete mode the recorded hover interval is replaced by
                // its bounded gap, rather than being waited a second time.
                if (options.Mode == ReplayMode.Discrete && !down && !sample.InContact) continue;
                await timer.WaitUntilAsync(clock, (sample.Ticks - stroke.StartTicks - removedIdleTicks) * 1000d / frequency / speed, token);
                clock.Stop();
                try { await ensureReady(sample); }
                finally { clock.Start(); }
                var point = new MappedPoint(
                    checked((int)Math.Round(sample.X)), checked((int)Math.Round(sample.Y)),
                    (uint)Math.Clamp(Math.Round(sample.Pressure * 1024), 0, 1024),
                    (int)Math.Clamp(Math.Round(sample.TiltX), -90, 90),
                    (int)Math.Clamp(Math.Round(sample.TiltY), -90, 90));
                if (sample.InContact)
                {
                    injector.Send(point, down ? PointerTransition.Update : PointerTransition.Down);
                    down = true;
                    last = point;
                }
                else if (down)
                {
                    injector.Send(point with { Pressure = 0 }, up);
                    down = false;
                    last = point;
                    TimeSpan gap = followingGap;
                    if (options.Mode == ReplayMode.Discrete)
                    {
                        var nextContact = stroke.Samples.Skip(sampleIndex + 1).FirstOrDefault(s => s.InContact);
                        if (nextContact is not null)
                        {
                            gap = options.GapBetween(sample.Ticks, nextContact.Ticks, frequency);
                            removedIdleTicks += Math.Max(0, nextContact.Ticks - sample.Ticks);
                        }
                    }
                    await SeparateAsync(gap);
                }
            }
            if (down)
            {
                await timer.WaitUntilAsync(clock, (stroke.EndTicks - stroke.StartTicks - removedIdleTicks) * 1000d / frequency / speed, token);
                await ensureReady(stroke.Samples[^1] with { InContact = false });
                injector.Send(last with { Pressure = 0 }, up);
                down = false;
                await SeparateAsync(followingGap);
            }
        }
        finally
        {
            if (down) injector.Send(last with { Pressure = 0 }, up);
        }
    }
    private sealed class SyntheticPenInjector : IPenInjector
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
                PointerTransition.UpAndLeave => PointerFlagUp,
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
                Pressure = transition is PointerTransition.Up or PointerTransition.UpAndLeave ? 0 : point.Pressure,
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
