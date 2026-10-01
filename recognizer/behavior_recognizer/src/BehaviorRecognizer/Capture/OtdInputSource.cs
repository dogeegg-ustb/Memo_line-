using System.Collections.Concurrent;
using BehaviorRecognizer.Abstractions.Input;
using OpenTabletDriver;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Tablet;

namespace BehaviorRecognizer.Capture;

/// <summary>
/// Embeds OpenTabletDriver <see cref="Driver"/> for device discovery and HID report reading.
/// Does not require a separately installed OpenTabletDriver desktop app.
/// </summary>
public sealed class OtdInputSource : IInputSource
{
    private readonly object _sync = new();
    private Driver? _driver;
    private readonly List<(InputDevice Device, EventHandler<IDeviceReport> Handler)> _subscriptions = [];
    private readonly ConcurrentDictionary<string, DetectedDeviceInfo> _devices = new();
    private volatile bool _running;
    private bool _logHooked;

    public string Name => "OpenTabletDriver.Embedded";

    public bool IsRunning => _running;

    public IReadOnlyList<DetectedDeviceInfo> DetectedDevices => _devices.Values.ToList();

    public event EventHandler<RawInputReport>? ReportReceived;

    public event EventHandler<DetectedDeviceInfo>? DeviceChanged;

    public Task<bool> DetectDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            EnsureDriver();
            ClearSubscriptions();
            _devices.Clear();

            var found = _driver!.Detect();
            AttachTrees(_driver.InputDevices);

            return Task.FromResult(found || _devices.Count > 0);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            EnsureDriver();
            if (_devices.Count == 0)
            {
                _driver!.Detect();
                AttachTrees(_driver.InputDevices);
            }

            _running = true;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _running = false;
            ClearSubscriptions();
            if (_driver is not null)
            {
                _driver.Dispose();
                _driver = null;
            }

            _devices.Clear();
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void EnsureDriver()
    {
        if (_driver is not null)
            return;

        if (!_logHooked)
        {
            Log.Output += (_, message) =>
            {
                if (message.Level is LogLevel.Error or LogLevel.Warning)
                    Console.Error.WriteLine($"[OTD:{message.Group}] {message.Message}");
            };
            _logHooked = true;
        }

        var builder = new DriverBuilder();
        _driver = builder.Build<Driver>(out _);
        _driver.TabletsChanged += (_, tablets) =>
        {
            foreach (var tablet in tablets)
            {
                var info = ToDeviceInfo(tablet);
                _devices[info.DeviceId] = info;
                DeviceChanged?.Invoke(this, info);
            }
        };
    }

    private void AttachTrees(IEnumerable<InputDeviceTree> trees)
    {
        foreach (var tree in trees)
        {
            var reference = tree.CreateReference();
            var info = ToDeviceInfo(reference);
            _devices[info.DeviceId] = info;
            DeviceChanged?.Invoke(this, info);

            var maxPressure = reference.Properties.Specifications.Pen.MaxPressure;
            var deviceId = info.DeviceId;

            foreach (var device in tree.InputDevices)
            {
                EventHandler<IDeviceReport> handler = (_, report) =>
                {
                    if (!_running)
                        return;

                    var raw = ConvertReport(deviceId, report, maxPressure);
                    ReportReceived?.Invoke(this, raw);
                };

                device.Report += handler;
                _subscriptions.Add((device, handler));
            }
        }
    }

    private void ClearSubscriptions()
    {
        foreach (var (device, handler) in _subscriptions)
        {
            try
            {
                device.Report -= handler;
            }
            catch
            {
                // Device may already be disposed.
            }
        }

        _subscriptions.Clear();
    }

    private static DetectedDeviceInfo ToDeviceInfo(TabletReference tablet)
    {
        var digitizer = tablet.Properties.Specifications.Digitizer;
        var pen = tablet.Properties.Specifications.Pen;
        var id = tablet.Properties.DigitizerIdentifiers.FirstOrDefault();

        return new DetectedDeviceInfo
        {
            DeviceId = BuildDeviceId(tablet.Properties.Name, id?.VendorID, id?.ProductID),
            Name = tablet.Properties.Name,
            Vendor = null,
            VendorId = id?.VendorID,
            ProductId = id?.ProductID,
            MaxPressure = pen.MaxPressure,
            Width = digitizer.Width,
            Height = digitizer.Height
        };
    }

    internal static string BuildDeviceId(string name, int? vendorId, int? productId)
        => $"{vendorId:X4}:{productId:X4}:{name}".Replace(' ', '_');

    internal static RawInputReport ConvertReport(string deviceId, IDeviceReport report, float maxPressure)
    {
        var now = DateTimeOffset.UtcNow;

        if (report is OutOfRangeReport)
        {
            return new RawInputReport
            {
                DeviceId = deviceId,
                Timestamp = now,
                IsOutOfRange = true,
                MaxPressure = maxPressure,
                RawBytes = report.Raw
            };
        }

        float? x = null, y = null, pressure = null, tiltX = null, tiltY = null;
        bool[]? buttons = null;
        var near = false;

        if (report is IAbsolutePositionReport abs)
        {
            x = abs.Position.X;
            y = abs.Position.Y;
        }

        if (report is ITabletReport tablet)
        {
            pressure = tablet.Pressure;
            buttons = tablet.PenButtons;
        }

        if (report is ITiltReport tilt)
        {
            tiltX = tilt.Tilt.X;
            tiltY = tilt.Tilt.Y;
        }

        if (report is IProximityReport proximity)
            near = proximity.NearProximity;

        return new RawInputReport
        {
            DeviceId = deviceId,
            Timestamp = now,
            X = x,
            Y = y,
            Pressure = pressure,
            MaxPressure = maxPressure,
            TiltX = tiltX,
            TiltY = tiltY,
            PenButtons = buttons,
            IsNearProximity = near,
            IsOutOfRange = false,
            RawBytes = report.Raw
        };
    }
}
