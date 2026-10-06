using System.Runtime.InteropServices;
using System.Threading.Channels;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Storage.Memoline;

namespace BehaviorRecognizer.Capture;

/// <summary>Interprets hardware transitions before persistence; one operation ID groups each gesture.</summary>
public sealed class UnifiedInputCapture : IAsyncDisposable
{
    private static readonly HardwareDeviceSource MouseSource = new("mouse", "Windows.LowLevelMouseHook", null, "classOnly");
    private static readonly HardwareDeviceSource KeyboardSource = new("keyboard", "Windows.LowLevelKeyboardHook", null, "classOnly");
    private static readonly HardwareDeviceSource PassivePenSource = new("pen", "Windows.LowLevelMouseHook", null, "penSignature");
    private readonly MemolineWriter _writer;
    private readonly UpdateActivatorBridge? _updates;
    private readonly IInputTargetProbe _targetProbe;
    private readonly Func<int, int, PenDownLocation> _locatePenDown;
    private readonly object _sync = new();
    private readonly Dictionary<int, ulong> _mouse = [];
    private readonly HashSet<int> _keysDown = [];
    private readonly HashSet<int> _usedModifiers = [];
    private readonly List<ulong> _penEvents = [];
    private ulong? _penOperation;
    private PenDownLocation? _penDownLocation;
    private bool _penPhysicalDown;
    private int _penX, _penY;
    private float? _penPressure, _penTiltX, _penTiltY;
    private float? _penTabletX, _penTabletY;
    private (int X, int Y, bool InCsp, bool Foreground, int Buttons, bool Pen)? _lastCursorState;
    private long _lastCursorStateTicks, _lastTabletHoverTicks;
    private bool _tabletNearCsp;
    private (float? X, float? Y, float? Pressure, float? TiltX, float? TiltY)? _lastTabletHover;
    private readonly Channel<CaptureWork> _pending = Channel.CreateUnbounded<CaptureWork>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _pollCancellation = new();
    private readonly Task _worker;
    private readonly Task _cursorPoller;

    public UnifiedInputCapture(MemolineWriter writer, UpdateActivatorBridge? updates = null,
        IInputTargetProbe? targetProbe = null, Func<int, int, PenDownLocation>? locatePenDown = null)
    {
        _writer = writer;
        _updates = updates;
        _targetProbe = targetProbe ?? new CspInputTargetProbe();
        _locatePenDown = locatePenDown ?? ((x, y) => _updates?.ClassifyPenDown(x, y) ?? PanelRegionMap.Unavailable().Classify(x, y));
        _worker = Task.Run(ProcessAsync);
        _cursorPoller = Task.Run(PollCursorAsync);
    }

    // Producers only enqueue: neither a Windows hook callback nor an OTD report thread
    // performs a process lookup, JSON serialization, or file write.
    public void PostPen(InputEvent evt) => _pending.Writer.TryWrite(new CaptureWork(evt, 0, 0, 0, 0, 0, 0, false, false, false));
    public void PostMouse(int message, int x, int y, int mouseData, bool passivePen = false) =>
        _pending.Writer.TryWrite(new CaptureWork(null, message, x, y, mouseData, 0, 0, false, false, passivePen));
    public void PostKey(int vk, uint scanCode, bool extended, bool pressed, bool injected = false, nuint extraInfo = 0, bool guardIntercepted = false, nint nextHookResult = 0) =>
        _pending.Writer.TryWrite(new CaptureWork(null, 0, 0, 0, 0, vk, scanCode, extended, pressed, false, injected, extraInfo, guardIntercepted, nextHookResult));

    private async Task ProcessAsync()
    {
        await foreach (var work in _pending.Reader.ReadAllAsync())
        {
            try
            {
                if (_updates is not null && !_updates.RecordingReady) continue;
                if (work.Pen is not null) OnPen(work.Pen);
                else if (work.PassivePen) OnPassivePenMouse(work.MouseMessage, work.X, work.Y);
                else if (work.MouseMessage != 0) OnMouse(work.MouseMessage, work.X, work.Y, work.MouseData);
                else OnKey(work.VirtualKey, work.ScanCode, work.Extended, work.Pressed, work.Injected, work.ExtraInfo, work.GuardIntercepted, work.NextHookResult);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[InputCapture] {ex.Message}"); }
        }
    }

    private async Task PollCursorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
            while (await timer.WaitForNextTickAsync(_pollCancellation.Token))
                RefreshCursor();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.Error.WriteLine($"[CursorMonitor] {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _pollCancellation.CancelAsync();
        await _cursorPoller;
        _pollCancellation.Dispose();
        _pending.Writer.TryComplete();
        await _worker;
    }

    private readonly record struct CaptureWork(InputEvent? Pen, int MouseMessage, int X, int Y,
        int MouseData, int VirtualKey, uint ScanCode, bool Extended, bool Pressed, bool PassivePen, bool Injected = false, nuint ExtraInfo = 0, bool GuardIntercepted = false, nint NextHookResult = 0);

    public void RefreshCursor()
    {
        lock (_sync)
        {
            bool foreground = _targetProbe.IsCspForeground();
            if (!foreground)
            {
                ResetKeyboard("cspLostFocus");
            }
            bool inCsp = _targetProbe.TryGetCspCursor(out var cursor);
            ulong[] operations = _mouse.Values.Concat(_penOperation is { } pen ? new[] { pen } : Array.Empty<ulong>()).ToArray();
            _updates?.ObserveCursor(cursor.X, cursor.Y, inCsp, foreground, _keysDown.Order().ToArray(), operations);
            var cursorState = (cursor.X, cursor.Y, inCsp, foreground,
                _mouse.Keys.Aggregate(0, (mask, key) => mask | (1 << key)), _penPhysicalDown);
            long now = _writer.NowTicks;
            bool boundaryChanged = _lastCursorState is { } previous &&
                (previous.InCsp != inCsp || previous.Foreground != foreground);
            if (_lastCursorState != cursorState && (inCsp || _lastCursorState is { InCsp: true }) &&
                (boundaryChanged || now - _lastCursorStateTicks >= System.Diagnostics.Stopwatch.Frequency / 30))
            {
                _writer.AppendState("mouseCursorChanged", now, [], new { x = cursor.X, y = cursor.Y,
                    inCsp, foreground, heldButtons = HeldMouseButtons(), heldKeys = HeldKeys(), penContact = _penPhysicalDown,
                    source = "windowsCursor" });
                _lastCursorState = cursorState;
                _lastCursorStateTicks = now;
            }
            if (inCsp) return;
            // Cursor exit invalidates all active pointer operations at the boundary.
            foreach (var operation in _mouse.Values.Distinct())
                _writer.AppendState("mouseInterrupted", _writer.NowTicks, [operation], new { reason = "cursorLeftCsp", heldButtons = Array.Empty<string>(), heldKeys = HeldKeys() });
            _mouse.Clear();
            InterruptPen("cursorLeftCsp");
        }
    }

    public void OnPen(InputEvent evt)
    {
        if (evt.Type is not (InputEventType.PenDown or InputEventType.PenMove or InputEventType.PenUp or InputEventType.PenHover or InputEventType.PenButtonChanged)) return;
        lock (_sync)
        {
            // Use the actual Windows target for UI activation, consistently with
            // cursor polling. Reconstructed driver coordinates remain in PenData.
            bool hovering = _targetProbe.TryGetCspCursor(out var cursor);
            if (evt.Type == InputEventType.PenButtonChanged)
            {
                if (hovering) _writer.AppendState("tabletStateChanged", _writer.NowTicks, [], new
                { action = "buttons", inCsp = true, sample = PenData(evt, cursor) });
                return;
            }
            if (evt.Type == InputEventType.PenHover)
            {
                var sampleState = (evt.Position?.X, evt.Position?.Y, evt.Pressure, evt.Tilt?.X, evt.Tilt?.Y);
                long now = _writer.NowTicks;
                bool near = hovering && evt.ContactState != ContactState.OutOfRange;
                if ((hovering || _tabletNearCsp) && (near != _tabletNearCsp ||
                    (near && _lastTabletHover != sampleState && now - _lastTabletHoverTicks >= System.Diagnostics.Stopwatch.Frequency / 30)))
                {
                    _writer.AppendState("tabletStateChanged", now, [], new
                    { action = !hovering ? "leave" : near ? "hover" : "outOfRange", inCsp = hovering, sample = PenData(evt, cursor) });
                    _lastTabletHover = sampleState;
                    _lastTabletHoverTicks = now;
                }
                _tabletNearCsp = near;
            }
            else _tabletNearCsp = hovering && evt.ContactState != ContactState.OutOfRange;
            bool down = evt.ContactState == ContactState.Contact && evt.Type != InputEventType.PenUp;
            if (!hovering)
            {
                InterruptPen("cursorLeftCsp");
                _penPhysicalDown = down;
                return;
            }
            if (!down)
            {
                if (_penOperation is not null)
                {
                    _writer.AppendHardware("penEnd", PenData(evt, cursor, "penUp"),
                        OtdPenSource(evt.DeviceId), _penOperation);
                    _penOperation = null;
                    _penDownLocation = null;
                    _penEvents.Clear();
                }
                _penPhysicalDown = false;
                return;
            }
            // A stroke that began outside CSP never starts in its middle.
            if (_penOperation is null && _penPhysicalDown) return;
            _penPhysicalDown = true;
            if (_penOperation is null)
            {
                foreach (var operation in _mouse.Values.Distinct())
                    _writer.AppendState("mouseInterrupted", _writer.NowTicks, [operation], new { reason = "penContact", heldButtons = Array.Empty<string>(), heldKeys = HeldKeys() });
                _mouse.Clear();
                _penDownLocation = _locatePenDown(cursor.X, cursor.Y);
                var start = _writer.AppendHardware("penBegin", PenData(evt, cursor), OtdPenSource(evt.DeviceId));
                _penOperation = start.EventId;
                _penEvents.Add(start.EventId);
                _penX = cursor.X; _penY = cursor.Y; _penPressure = evt.Pressure;
                _penTiltX = evt.Tilt?.X; _penTiltY = evt.Tilt?.Y;
                _penTabletX = evt.Position?.X; _penTabletY = evt.Position?.Y;
                return;
            }
            if (cursor.X == _penX && cursor.Y == _penY && evt.Pressure == _penPressure &&
                evt.Position?.X == _penTabletX && evt.Position?.Y == _penTabletY &&
                evt.Tilt?.X == _penTiltX && evt.Tilt?.Y == _penTiltY && evt.Type != InputEventType.PenDown) return;
            var sample = _writer.AppendHardware("penSample", PenData(evt, cursor),
                OtdPenSource(evt.DeviceId), _penOperation);
            _penEvents.Add(sample.EventId);
            _penX = cursor.X; _penY = cursor.Y; _penPressure = evt.Pressure;
            _penTiltX = evt.Tilt?.X; _penTiltY = evt.Tilt?.Y;
            _penTabletX = evt.Position?.X; _penTabletY = evt.Position?.Y;
        }
    }

    private int[] HeldKeys() => _keysDown.Order().ToArray();
    private string[] HeldMouseButtons() => _mouse.Keys.Order().Select(ButtonName).ToArray();

    private object PenData(InputEvent evt, CspWindowProbe.ScreenPoint cursor, string? reason = null) => new
    {
        x = cursor.X, y = cursor.Y, tabletX = evt.Position?.X, tabletY = evt.Position?.Y,
        pressure = evt.Pressure, tiltX = evt.Tilt?.X, tiltY = evt.Tilt?.Y,
        contactState = evt.ContactState.ToString(),
        interactionCoordinateSource = "windowsCursor",
        normalizedPressure = evt.NormalizedPressure, mappedPressure = evt.DriverMapping?.MappedPressure,
        physicalX = evt.DriverMapping?.PhysicalX, physicalY = evt.DriverMapping?.PhysicalY,
        screenX = evt.DriverMapping?.ScreenX ?? cursor.X, screenY = evt.DriverMapping?.ScreenY ?? cursor.Y,
        screenCoordinateSource = evt.DriverMapping?.CoordinateStatus == "ready" ? "driverMapping" : "windowsCursor",
        driverSnapshotId = evt.DriverMapping?.DriverSnapshotId,
        pressureMappingStatus = evt.DriverMapping?.PressureStatus ?? "notSelected",
        coordinateMappingStatus = evt.DriverMapping?.CoordinateStatus ?? "notSelected", reason,
        deviceId = evt.DeviceId, penButtons = evt.PenButtons, heldKeys = HeldKeys(), penDownLocation = _penDownLocation
    };

    private static HardwareDeviceSource OtdPenSource(string deviceId) =>
        new("pen", "OpenTabletDriver.HID", deviceId, "deviceId");

    private void InterruptPen(string reason)
    {
        if (_penOperation is null) return;
        _writer.AppendState("penInterrupted", _writer.NowTicks, _penEvents.ToArray(), new { reason, penDownLocation = _penDownLocation });
        _penOperation = null;
        _penDownLocation = null;
        _penEvents.Clear();
    }

    private void OnPassivePenMouse(int message, int x, int y)
    {
        if (message is not (0x0200 or 0x0201 or 0x0202)) return;
        lock (_sync)
        {
            if (!_targetProbe.IsCspPoint(x, y))
            {
                InterruptPen("cursorLeftCsp");
                if (message == 0x0201) _penPhysicalDown = true;
                if (message == 0x0202) _penPhysicalDown = false;
                return;
            }
            if (message == 0x0201)
            {
                if (_penPhysicalDown) return;
                _penPhysicalDown = true;
                foreach (var operation in _mouse.Values.Distinct())
                    _writer.AppendState("mouseInterrupted", _writer.NowTicks, [operation], new { reason = "penContact", heldButtons = Array.Empty<string>(), heldKeys = HeldKeys() });
                _mouse.Clear();
                _penDownLocation = _locatePenDown(x, y);
                var start = _writer.AppendHardware("penBegin", new
                {
                    x, y, pressure = (float?)null, deviceId = "windows-pen",
                    source = "windowsPenCompatibility", heldKeys = HeldKeys(), penDownLocation = _penDownLocation
                }, PassivePenSource);
                _penOperation = start.EventId;
                _penEvents.Add(start.EventId);
                _penX = x; _penY = y;
            }
            else if (message == 0x0200 && _penOperation is not null && (x != _penX || y != _penY))
            {
                var sample = _writer.AppendHardware("penSample", new
                {
                    x, y, pressure = (float?)null, deviceId = "windows-pen",
                    source = "windowsPenCompatibility", heldKeys = HeldKeys(), penDownLocation = _penDownLocation
                }, PassivePenSource, _penOperation);
                _penEvents.Add(sample.EventId);
                _penX = x; _penY = y;
            }
            else if (message == 0x0202)
            {
                _penPhysicalDown = false;
                if (_penOperation is not null)
                    _writer.AppendHardware("penEnd", new { x, y, reason = "penUp", heldKeys = HeldKeys(), penDownLocation = _penDownLocation },
                        PassivePenSource, _penOperation);
                _penOperation = null;
                _penDownLocation = null;
                _penEvents.Clear();
            }
        }
    }

    public void OnMouse(int message, int x, int y, int mouseData)
    {
        lock (_sync)
        {
            var hovering = _targetProbe.IsCspPoint(x, y);
            if (message is 0x020A or 0x020E)
            {
                int delta = (short)((mouseData >> 16) & 0xffff);
                if (hovering && delta != 0)
                    _writer.AppendHardware("mouseWheel", new { x, y, delta,
                        axis = message == 0x020A ? "vertical" : "horizontal", heldKeys = HeldKeys(), heldButtons = HeldMouseButtons() }, MouseSource);
                return;
            }
            // A tablet may also move the Windows mouse cursor. Its contact is already
            // represented by pen frames, so avoid a second mouse gesture for it.
            if (_penOperation is not null) return;
            if (!hovering)
            {
                foreach (var operation in _mouse.Values.Distinct())
                    _writer.AppendState("mouseInterrupted", _writer.NowTicks, [operation], new { reason = "cursorLeftCsp", heldButtons = Array.Empty<string>(), heldKeys = HeldKeys() });
                _mouse.Clear();
                return;
            }
            if (message == 0x0200) // WM_MOUSEMOVE
            {
                foreach (var (heldButton, operation) in _mouse)
                    _writer.AppendHardware("mouseDrag", new { button = ButtonName(heldButton), x, y, heldKeys = HeldKeys(), heldButtons = HeldMouseButtons() },
                        MouseSource, operation);
                return;
            }
            int button = message switch
            {
                0x0201 or 0x0202 => 1, 0x0204 or 0x0205 => 2, 0x0207 or 0x0208 => 3,
                0x020B or 0x020C => 4 + ((mouseData >> 16) & 0xffff), _ => 0
            };
            if (button == 0) return;
            bool pressed = message is 0x0201 or 0x0204 or 0x0207 or 0x020B;
            if (pressed)
            {
                if (_mouse.ContainsKey(button)) return;
                var stamp = _writer.AppendHardware("mouseDown", new { button = ButtonName(button), x, y, heldKeys = HeldKeys(),
                    heldButtons = _mouse.Keys.Append(button).Order().Select(ButtonName).ToArray() }, MouseSource);
                _mouse[button] = stamp.EventId;
            }
            else if (_mouse.Remove(button, out var operation))
                _writer.AppendHardware("mouseUp", new { button = ButtonName(button), x, y, heldKeys = HeldKeys(), heldButtons = HeldMouseButtons() }, MouseSource, operation);
        }
    }

    private static string ButtonName(int button) => button switch { 1 => "left", 2 => "right", 3 => "middle", 5 => "x1", 6 => "x2", _ => $"button{button}" };

    public void OnKey(int vk, uint scanCode, bool extended, bool pressed, bool injected = false, nuint extraInfo = 0, bool guardIntercepted = false, nint nextHookResult = 0)
    {
        lock (_sync)
        {
            bool focused = _targetProbe.IsCspForeground();
            if (!focused)
            {
                ResetKeyboard("cspLostFocus");
                return;
            }
            bool modifier = IsModifier(vk);
            if (pressed)
            {
                bool repeat = !_keysDown.Add(vk);
                RecordKeyboardTransition("keyDown", vk, scanCode, extended, true, repeat, injected, extraInfo, guardIntercepted, nextHookResult);
                if (modifier) return;
                var modifiers = _keysDown.Where(IsModifier).Order().Select(KeyName).ToArray();
                foreach (var key in _keysDown.Where(IsModifier)) _usedModifiers.Add(key);
                _writer.AppendHardware("keyInput", new { key = KeyName(vk), vk, scanCode, extended, injected, extraInfo = $"0x{extraInfo:X}", guardIntercepted, nextHookResult = nextHookResult.ToInt64(),
                    modifiers, combination = modifiers.Length > 0, repeat, heldKeys = HeldKeys(),
                    shortcutMatch = (string?)null }, KeyboardSource);
                Console.WriteLine($"[键盘监视] {string.Join("+", modifiers.Append(KeyName(vk)))} 注入={injected} 图层保护拦截={guardIntercepted} 后续Hook阻止={!guardIntercepted && nextHookResult != 0} 来源标记=0x{extraInfo:X}");
            }
            else
            {
                if (!_keysDown.Remove(vk)) return;
                RecordKeyboardTransition("keyUp", vk, scanCode, extended, false, false, injected, extraInfo, guardIntercepted, nextHookResult);
                if (modifier && !_usedModifiers.Remove(vk))
                    _writer.AppendHardware("keyInput", new { key = KeyName(vk), vk, scanCode, extended, injected, extraInfo = $"0x{extraInfo:X}", guardIntercepted, nextHookResult = nextHookResult.ToInt64(),
                        modifiers = Array.Empty<string>(), combination = false, repeat = false, heldKeys = HeldKeys(),
                        shortcutMatch = (string?)null }, KeyboardSource);
            }
        }
    }

    private void RecordKeyboardTransition(string action, int vk, uint scanCode, bool extended, bool pressed,
        bool repeat, bool injected, nuint extraInfo, bool guardIntercepted, nint nextHookResult) =>
        _writer.AppendState("keyboardStateChanged", _writer.NowTicks, [], new
        {
            action, key = KeyName(vk), vk, scanCode, extended, pressed, repeat, injected,
            extraInfo = $"0x{extraInfo:X}", guardIntercepted, nextHookResult = nextHookResult.ToInt64(),
            isModifier = IsModifier(vk), heldKeys = HeldKeys(), modifiers = _keysDown.Where(IsModifier).Order().Select(KeyName).ToArray(),
            deviceSource = KeyboardSource
        });

    private void ResetKeyboard(string reason)
    {
        if (_keysDown.Count > 0) _writer.AppendState("keyboardStateChanged", _writer.NowTicks, [], new
        { action = "reset", reason, heldKeys = Array.Empty<int>(), modifiers = Array.Empty<string>(), deviceSource = KeyboardSource });
        _keysDown.Clear();
        _usedModifiers.Clear();
    }

    private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
    private static string KeyName(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => "Shift", 0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x12 or 0xA4 or 0xA5 => "Alt", 0x5B or 0x5C => "Win",
        0x20 => "Space", 0x0D => "Enter", 0x09 => "Tab", 0x1B => "Escape",
        0xDB => "[", 0xDD => "]", 0xBC => ",", 0xBE => ".",
        0xBB => "OEM_PLUS", 0xBD => "OEM_MINUS", 0x6B => "NUM+", 0x6D => "NUM-",
        >= 0x30 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => $"VK_{vk:X2}"
    };
}
