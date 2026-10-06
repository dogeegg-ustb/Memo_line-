using System.Diagnostics;
using BehaviorRecognizer.Abstractions.Config;
using BehaviorRecognizer.Abstractions.Environment;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Abstractions.Recording;
using BehaviorRecognizer.Abstractions.Session;
using BehaviorRecognizer.Abstractions.Storage;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Recording;
using BehaviorRecognizer.Session;
using BehaviorRecognizer.Storage.Memoline;
using BehaviorRecognizer.Realtime;

namespace BehaviorRecognizer.Bootstrap;

/// <summary>
/// 启动编排：环境探测 → 配置 → 采集流水线 → 会话 → 记录器。
/// 可选能力（vMulti / WinInk）缺失不得阻断基础采集。
/// </summary>
public sealed class CapabilityOrchestrator
{
    private readonly ApplicationPaths _paths; // 路径布局
    private readonly CaptureOptions _captureOptions;
    private readonly IEnvironmentProbe _environmentProbe; // 环境探测
    private readonly IPenProfileProvider _profileProvider; // 笔配置
    private readonly IDeviceProfileMatcher _profileMatcher; // 设备匹配
    private readonly IConfigurationSnapshotProvider _snapshotProvider; // 配置快照
    private readonly IInputSource _inputSource; // 输入源
    private readonly IInputEventBus _eventBus; // 事件总线
    private readonly ISessionManager _sessionManager; // 会话管理
    private readonly IRecorderBus _recorderBus; // 记录器总线
    private readonly IRecoveryReader _recoveryReader; // 恢复扫描
    private readonly IVMultiDetector _vMultiDetector; // vMulti 检测

    private MemolineWriter? _memoline;
    private UnifiedInputCapture? _unifiedCapture;
    private WindowsInputHooks? _windowsHooks;
    private UpdateActivatorBridge? _updateActivator;
    private InputEventNormalizer? _normalizer; // 事件归一化
    private DriverInitializationService? _driverInitialization;
    private RecorderRealtimeHub? _realtime;
    private RecorderRealtimePipeServer? _realtimeServer;
    private RecorderInputControlServer? _inputControlServer;
    private ulong _sequence; // 会话序号
    private ConfigurationSnapshot? _configSnapshot; // 配置快照
    private EnvironmentSnapshot? _environmentSnapshot; // 环境快照

    public CapabilityOrchestrator(
        ApplicationPaths paths,
        CaptureOptions captureOptions,
        IEnvironmentProbe environmentProbe,
        IPenProfileProvider profileProvider,
        IDeviceProfileMatcher profileMatcher,
        IConfigurationSnapshotProvider snapshotProvider,
        IInputSource inputSource,
        IInputEventBus eventBus,
        ISessionManager sessionManager,
        IRecorderBus recorderBus,
        IRecoveryReader recoveryReader,
        IVMultiDetector vMultiDetector)
    {
        _paths = paths;
        _captureOptions = captureOptions;
        _environmentProbe = environmentProbe;
        _profileProvider = profileProvider;
        _profileMatcher = profileMatcher;
        _snapshotProvider = snapshotProvider;
        _inputSource = inputSource;
        _eventBus = eventBus;
        _sessionManager = sessionManager;
        _recorderBus = recorderBus;
        _recoveryReader = recoveryReader;
        _vMultiDetector = vMultiDetector;
    }

    public EnvironmentSnapshot? LastEnvironment => _environmentSnapshot;
    public ConfigurationSnapshot? LastConfiguration => _configSnapshot;
    public IRecorderInputControl? InputControl => _updateActivator?.Guard;
    public string? InputControlPipeName => _inputControlServer?.PipeName;

    /// <summary>启动统一输入采集与 .memoline 写入。</summary>
    public async Task<SessionInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        var session = await _sessionManager.CreateAsync(cancellationToken);
        await _sessionManager.TransitionAsync(SessionState.Initializing, cancellationToken);

        // Incomplete sessions remain available for frame-by-frame recovery.
        var leftover = await _recoveryReader.RecoverPartFilesAsync(
            Path.Combine(_paths.StrokeRoot, "stroke"), cancellationToken);
        if (leftover > 0)
            Console.WriteLine($"发现 {leftover} 个未完整提交的 .part 文件（已保留，未改名）。");

        // 1) 加载笔配置
        var defaults = _profileProvider.GetDefaultProfile();
        var presets = _profileProvider.GetDevicePresets();
        var user = _profileProvider.TryLoadUserProfile();
        var configPresent = _profileProvider.HasDefaultConfigFile;

        // Preserve the existing OTD capture path. Passive capture is an explicit fallback.
        var detected = _captureOptions.EnableOtdHid && await _inputSource.DetectDevicesAsync(cancellationToken);
        if (_captureOptions.EnableOtdHid && _captureOptions.TabletDeviceId is not null && !detected)
            throw new InvalidOperationException("未找到所选数位板：" + _captureOptions.TabletDeviceId + "。请连接设备后刷新设备列表。");
        var device = _captureOptions.EnableOtdHid ? _inputSource.DetectedDevices.FirstOrDefault() : null;
        var deviceName = device?.Name;
        var deviceId = device?.DeviceId ?? deviceName ?? "unknown";
        _driverInitialization = new DriverInitializationService(device);
        var profile = _profileMatcher.Match(defaults, presets, deviceName, user);
        var source = user is not null ? "user-override"
            : profile.ProfileId != defaults.ProfileId ? "device-preset"
            : "builtin-default";
        _configSnapshot = _snapshotProvider.CreateSnapshot(profile, source);

        // 3) 环境探测（仅提示）
        _environmentSnapshot = _environmentProbe.Probe(
            tabletDevicePresent: detected || _inputSource.DetectedDevices.Count > 0,
            defaultConfigPresent: configPresent);

        PrintStatus();

        // Both immediate hardware input and future interpreted state append through one writer.
        _memoline = new MemolineWriter(Path.Combine(_paths.StrokeRoot, "stroke"),
            new { deviceName = deviceName ?? "unknown", deviceId, profileId = profile.ProfileId },
            new() { SuccessfulStatesOnly = true });
        Console.WriteLine($"[状态诊断日志] {_memoline.DiagnosticFilePath}");
        _realtime = new RecorderRealtimeHub(_memoline);
        try { _realtimeServer = new RecorderRealtimePipeServer(_realtime); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Realtime] 实时订阅接口不可用: {ex.Message}");
            await _realtime.DisposeAsync();
            _realtime = null;
        }
        foreach (var tablet in _inputSource.DetectedDevices)
            _memoline.AppendState("tabletDeviceChanged", _memoline.NowTicks, [], tablet, "immediate");
        _updateActivator = UpdateActivatorBridge.Start(_memoline, _driverInitialization);
        if (_updateActivator is not null)
            _inputControlServer = new RecorderInputControlServer(_updateActivator.Guard,
                () => _updateActivator?.RecordingReady == true, recording: _memoline, requestStates: _updateActivator.RequestStatesAsync);
        _unifiedCapture = new UnifiedInputCapture(_memoline, _updateActivator);
        _windowsHooks = new WindowsInputHooks(_unifiedCapture, passivePen: !_captureOptions.EnableOtdHid, _updateActivator?.Guard);

        _normalizer = new InputEventNormalizer(profile, () => _driverInitialization.Current);
        _inputSource.ReportReceived += OnReport;
        _inputSource.DeviceChanged += OnDeviceChanged;


        if (_captureOptions.EnableOtdHid)
            await _inputSource.StartAsync(cancellationToken);
        await _sessionManager.TransitionAsync(SessionState.Ready, cancellationToken);
        await _sessionManager.TransitionAsync(SessionState.Recording, cancellationToken);

        Console.WriteLine();
        Console.WriteLine("正在初始化状态；初始状态包登记后开始录制 CSP 硬件输入。请等待初始化完成提示。");
        Console.WriteLine(_captureOptions.EnableOtdHid
            ? "笔来源: OTD 原始笔报告（保留压力与倾斜数据）"
            : "笔来源: Windows 被动笔事件（不打开数位板 HID；压力可能不可用）");
        Console.WriteLine($"会话: {_memoline.SessionId}");
        Console.WriteLine($"输出: {_memoline.FilePath}");
        Console.WriteLine($"实时读取: {_memoline.LiveFilePath}");
        if (_realtimeServer is not null)
        {
            Console.WriteLine($"实时订阅管道: {_realtimeServer.PipeName}");
            Console.WriteLine($"接口描述: {_realtimeServer.ManifestPath}");
        }
        Console.WriteLine("按 Enter 停止。");
        if (_inputControlServer is not null)
            Console.WriteLine($"输入控制与一次性保存管道: {_inputControlServer.PipeName}");

        return session;
    }

    /// <summary>停止采集并完整关闭 .memoline 会话。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _inputSource.ReportReceived -= OnReport;
        _inputSource.DeviceChanged -= OnDeviceChanged;
        _windowsHooks?.Dispose();
        _windowsHooks = null;
        await _inputSource.StopAsync(cancellationToken);
        if (_unifiedCapture is not null) await _unifiedCapture.DisposeAsync();
        if (_inputControlServer is not null) await _inputControlServer.DisposeAsync();
        if (_updateActivator is not null) await _updateActivator.DisposeAsync();
        if (_memoline is not null) await _memoline.DisposeAsync();
        if (_realtime is not null) await _realtime.DisposeAsync();
        if (_realtimeServer is not null) await _realtimeServer.DisposeAsync();
        await _eventBus.DisposeAsync();

        if (_sessionManager.Current is not null &&
            _sessionManager.Current.State != SessionState.Stopped)
        {
            await _sessionManager.TransitionAsync(SessionState.Stopped, cancellationToken);
        }
    }

    public void OpenVMultiInstallGuide()
    {
        var guide = _vMultiDetector.CreateInstallGuide();
        Console.WriteLine();
        Console.WriteLine($"[{guide.Title}] {guide.Message}");
        if (!string.IsNullOrWhiteSpace(guide.InstallerUrl))
        {
            Console.WriteLine($"安装引导: {guide.InstallerUrl}");
            try
            {
                Process.Start(new ProcessStartInfo(guide.InstallerUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"无法打开浏览器: {ex.Message}");
            }
        }
    }

    private void PrintStatus()
    {
        var env = _environmentSnapshot!;
        Console.WriteLine("======== BehaviorRecognizer 状态 ========");
        Console.WriteLine(!_captureOptions.EnableOtdHid
            ? "被动笔监视已启用（不打开 HID 设备）"
            : env.TabletDevicePresent ? "检测到设备" : "未检测到设备（将继续等待）");
        Console.WriteLine($"当前配置已应用: {_configSnapshot!.AppliedProfile.DisplayName} ({_configSnapshot.Source})");
        Console.WriteLine($"Windows Ink: {DescribeInk(env.WindowsInk)}");
        Console.WriteLine($"vMulti: {DescribeVMulti(env.VMulti)}");

        foreach (var guide in env.Guides)
        {
            if (!_captureOptions.EnableOtdHid && guide.Title == "未检测到数位板")
                continue; // Passive mode deliberately skips HID enumeration.
            Console.WriteLine($"- {guide.Title}: {guide.Message}");
            if (!string.IsNullOrWhiteSpace(guide.InstallerUrl))
                Console.WriteLine($"  引导链接: {guide.InstallerUrl}");
        }

        Console.WriteLine("========================================");
    }

    private static string DescribeInk(WindowsInkStatus status) => status switch
    {
        WindowsInkStatus.Available => "可用",
        WindowsInkStatus.Unavailable => "不可用",
        WindowsInkStatus.NotApplicable => "不适用（非 Windows）",
        _ => "未知"
    };

    private static string DescribeVMulti(VMultiStatus status) => status switch
    {
        VMultiStatus.Installed => "已安装",
        VMultiStatus.NotInstalled => "未安装（可跳过）",
        VMultiStatus.InstalledButInactive => "已安装但未激活",
        VMultiStatus.PermissionDenied => "权限不足，无法完整检测",
        _ => "未知"
    };

    private void OnReport(object? sender, RawInputReport report)
    {
        var sessionId = _memoline?.SessionId
            ?? _sessionManager.Current?.SessionId
            ?? "unknown";
        var seq = Interlocked.Increment(ref _sequence);
        foreach (var evt in _normalizer!.Normalize(report, sessionId, seq))
            _unifiedCapture?.PostPen(evt);
    }

    private void OnDeviceChanged(object? sender, DetectedDeviceInfo device)
    {
        _memoline?.AppendState("tabletDeviceChanged", _memoline.NowTicks, [], device, "immediate");
        var sessionId = _memoline?.SessionId
            ?? _sessionManager.Current?.SessionId
            ?? "unknown";
        _eventBus.Publish(new InputEvent
        {
            Type = InputEventType.TabletDetected,
            Timestamp = DateTimeOffset.UtcNow,
            SessionId = sessionId,
            DeviceId = device.DeviceId,
            Sequence = Interlocked.Increment(ref _sequence),
            Message = device.Name,
            ContactState = ContactState.OutOfRange,
            Extensions = new Dictionary<string, object?>
            {
                ["width"] = device.Width,
                ["height"] = device.Height,
                ["maxPressure"] = device.MaxPressure
            }
        });
    }
}
