using System.Diagnostics;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.DependencyInjection;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;
using OtdStrokeRecorder.Plugin.Native;

namespace OtdStrokeRecorder.Plugin;

/// <summary>
/// Receives tablet reports from OpenTabletDriver's OutputMode pipeline.
/// Contract: IPositionedPipelineElement&lt;IDeviceReport&gt; (there is no IFilter).
/// Call chain: InputDeviceTree → IOutputMode.Read → Emit/Consume pipeline.
/// </summary>
[PluginName("ART Stroke Recorder")]
[SupportedPlatform(PluginPlatform.Windows)]
public sealed class StrokeRecorderFilter : IPositionedPipelineElement<IDeviceReport>, IDisposable
{
    private readonly object _gate = new();
    private IntPtr _native;
    private bool _started;
    private bool _penDown;
    private ulong _sequence;
    private ulong _consumeCount;
    private Timer? _tickTimer;
    private string _activeRoot = string.Empty;
    private string _deviceName = "OpenTabletDriver";
    private string _deviceId = "otd-filter";

    public event Action<IDeviceReport>? Emit;

    /// <summary>
    /// PreTransform: raw tablet units, before area limiting / transform.
    /// </summary>
    public PipelinePosition Position => PipelinePosition.PreTransform;

    [BooleanProperty("Enable Recording", "")]
    [DefaultPropertyValue(true)]
    public bool EnableRecording { get; set; } = true;

    [Property("Output Root (stroke/ under this path)")]
    [DefaultPropertyValue("")]
    [ToolTip("Leave empty to use %LocalAppData%\\OpenTabletDriver. Files go to <root>\\stroke\\")]
    public string OutputRoot { get; set; } = string.Empty;

    [TabletReference]
    public TabletReference? Tablet { get; set; }

    [OnDependencyLoad]
    public void OnLoad()
    {
        if (Tablet != null)
        {
            _deviceName = string.IsNullOrWhiteSpace(Tablet.Properties?.Name)
                ? "OpenTabletDriver"
                : Tablet.Properties.Name;
            _deviceId = Tablet.Properties?.Name ?? "otd-filter";
        }

        Log.Write("ART Stroke Recorder",
            $"Filter constructed (device={_deviceName}). Waiting for IDeviceReport via pipeline Consume.",
            LogLevel.Info);

        try
        {
            if (EnableRecording)
            {
                EnsureNative();
            }
        }
        catch (Exception ex)
        {
            Log.Write("ART Stroke Recorder", $"Native init on load failed: {ex.Message}", LogLevel.Error);
        }
    }

    public void Consume(IDeviceReport report)
    {
        _consumeCount++;
        if (_consumeCount == 1 || _consumeCount % 500 == 0)
        {
            Log.Write("ART Stroke Recorder",
                $"Consume #{_consumeCount} type={report.GetType().Name}",
                LogLevel.Debug);
        }

        try
        {
            if (EnableRecording)
            {
                Record(report);
            }
        }
        catch (Exception ex)
        {
            Log.Write("ART Stroke Recorder", $"Record failed: {ex.Message}", LogLevel.Error);
        }

        // Must forward or the rest of the OTD pipeline stops.
        Emit?.Invoke(report);
    }

    private void Record(IDeviceReport report)
    {
        EnsureNative();

        if (!_started || _native == IntPtr.Zero)
        {
            return;
        }

        var tipDown = false;
        double x = 0, y = 0, pressure = 0, tiltX = 0, tiltY = 0;
        uint buttons = 0;

        if (report is IAbsolutePositionReport abs)
        {
            x = abs.Position.X;
            y = abs.Position.Y;
        }

        if (report is ITabletReport tablet)
        {
            pressure = tablet.Pressure;
            tipDown = tablet.Pressure > 0;
            if (tablet.PenButtons != null)
            {
                for (var i = 0; i < tablet.PenButtons.Length && i < 32; i++)
                {
                    if (tablet.PenButtons[i])
                    {
                        buttons |= 1u << i;
                    }
                }
            }
        }

        if (report is ITiltReport tilt)
        {
            tiltX = tilt.Tilt.X;
            tiltY = tilt.Tilt.Y;
        }

        // Out of range → force pen up so flush can trigger.
        if (report is OutOfRangeReport && _penDown)
        {
            tipDown = false;
        }

        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _sequence++;

        lock (_gate)
        {
            if (tipDown && !_penDown)
            {
                OtdStrokeNative.otd_stroke_pen_down(_native);
                _penDown = true;
            }
            else if (!tipDown && _penDown)
            {
                OtdStrokeNative.otd_stroke_pen_up(_native);
                _penDown = false;
            }

            // Only persist points while in stroke (spec: stroke-based).
            if (_penDown || tipDown)
            {
                OtdStrokeNative.otd_stroke_on_point(
                    _native,
                    nowMs,
                    x,
                    y,
                    pressure,
                    tipDown ? 1 : 0,
                    buttons,
                    tiltX,
                    tiltY,
                    _sequence);
            }
        }
    }

    private void EnsureNative()
    {
        lock (_gate)
        {
            var root = ResolveOutputRoot();
            if (_native != IntPtr.Zero && _started &&
                string.Equals(root, _activeRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DisposeNativeUnlocked();
            OtdStrokeNative.EnsureLoaded();

            _activeRoot = root;
            var strokeDir = Path.Combine(root, "stroke");
            Directory.CreateDirectory(strokeDir);

            _native = OtdStrokeNative.otd_stroke_create(root, _deviceName, _deviceId);
            if (_native == IntPtr.Zero)
            {
                throw new InvalidOperationException("otd_stroke_create returned null");
            }

            if (OtdStrokeNative.otd_stroke_start(_native) == 0)
            {
                OtdStrokeNative.otd_stroke_destroy(_native);
                _native = IntPtr.Zero;
                throw new InvalidOperationException("otd_stroke_start failed");
            }

            _started = true;
            _penDown = false;
            _tickTimer = new Timer(_ =>
            {
                lock (_gate)
                {
                    if (_native != IntPtr.Zero)
                    {
                        OtdStrokeNative.otd_stroke_tick(_native, 0);
                    }
                }
            }, null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

            Log.Write("ART Stroke Recorder",
                $"Session started. Writing under {strokeDir}",
                LogLevel.Info);
        }
    }

    private string ResolveOutputRoot()
    {
        if (!string.IsNullOrWhiteSpace(OutputRoot))
        {
            return Path.GetFullPath(OutputRoot);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "OpenTabletDriver");
    }

    private void DisposeNativeUnlocked()
    {
        _tickTimer?.Dispose();
        _tickTimer = null;

        if (_native != IntPtr.Zero)
        {
            try
            {
                if (_penDown)
                {
                    OtdStrokeNative.otd_stroke_pen_up(_native);
                }
                if (_started)
                {
                    OtdStrokeNative.otd_stroke_stop(_native);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            OtdStrokeNative.otd_stroke_destroy(_native);
            _native = IntPtr.Zero;
        }

        _started = false;
        _penDown = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DisposeNativeUnlocked();
        }
    }
}
