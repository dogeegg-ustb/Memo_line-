using System.Runtime.InteropServices;
using System.Text.Json;
using BehaviorRecognizer.Abstractions.Input;
using DriverReader;

namespace BehaviorRecognizer.Capture;

/// <summary>Owns the user-selected driver mapping for this recording session.</summary>
public sealed class DriverInitializationService
{
    private readonly TabletDeviceInfo? _device;
    private readonly List<DriverProfile> _profiles = [];
    private readonly List<string> _warnings = [];
    private DriverMappingSession? _current;
    public DriverMappingSession? Current => Volatile.Read(ref _current);
    public IReadOnlyList<DisplayInfo> Displays { get; }

    public DriverInitializationService(DetectedDeviceInfo? device = null)
    {
        _device = device is null ? null : new(device.DeviceId, device.Name, device.Width, device.Height,
            device.MaxX, device.MaxY, device.MaxPressure);
        Displays = EnumerateDisplays();
        foreach (string path in DriverProfileParser.AutoDetectCandidatePaths(includeBackups: false))
        {
            try { _profiles.Add(DriverProfileParser.Parse(path, device?.Name)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                or ArgumentException or InvalidOperationException or System.Xml.XmlException)
            { _warnings.Add($"配置读取失败 {path}: {ex.Message}"); }
        }
    }

    public object Catalog => new
    {
        device = _device, displays = Displays, warnings = _warnings,
        choices = _profiles.Select(p => new { path = p.ConfigPath, vendor = p.Vendor,
            deviceName = p.DeviceName, curveSummary = p.CurveSummary })
    };

    public DriverConfigurationSnapshot Select(string? path, int? screenIndex = null)
    {
        var options = new DriverInitializationOptions
        {
            ConfigPath = path, Device = _device, Displays = Displays, Profiles = _profiles,
            CandidatePaths = [], ScreenIndexOverride = screenIndex, ReloadSelectedConfiguration = true
        };
        var session = path is null ? DriverMappingSession.Disabled(options) : DriverMappingSession.Initialize(options);
        if (session.Snapshot.Status == "selected" && path is not null)
        {
            Console.WriteLine($"[Driver] 用户选用配置: {path}");
            Console.WriteLine($"[Driver] 压力映射: {session.Snapshot.PressureMappingStatus}；坐标映射: {session.Snapshot.CoordinateMappingStatus}");
            foreach (var warning in session.Snapshot.Warnings) Console.Error.WriteLine($"[Driver] {warning}");
        }
        if (session.Snapshot.Status is "selected" or "disabled") Volatile.Write(ref _current, session);
        return session.Snapshot;
    }

    private static IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var result = new List<(string Name, bool Primary, RectArea Bounds)>();
        Native.MonitorCallback callback = (monitor, _, _, _) =>
        {
            var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
            if (Native.GetMonitorInfo(monitor, ref info))
                result.Add((info.DeviceName, (info.Flags & 1) != 0,
                    new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom)));
            return true;
        };
        Native.EnumDisplayMonitors(0, 0, callback, 0);
        return result.OrderByDescending(d => d.Primary).ThenBy(d => d.Name, StringComparer.Ordinal)
            .Select((d, index) => new DisplayInfo(index, d.Name, d.Primary, d.Bounds)).ToArray();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MonitorInfo
        {
            public uint Size; public Rect Monitor, Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }
        public delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint data);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    }
}
