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

namespace BehaviorRecognizer.Bootstrap;

/// <summary>
/// 启动编排：环境探测 → 配置 → 采集流水线 → 会话 → 记录器。
/// 可选能力（vMulti / WinInk）缺失不得阻断基础采集。
/// </summary>
public sealed class CapabilityOrchestrator
{
    private readonly ApplicationPaths _paths; // 路径布局
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

    private PenInputRecorder? _penRecorder; // 笔迹记录器
    private InputEventNormalizer? _normalizer; // 事件归一化
    private IDisposable? _busSubscription; // 总线订阅
    private ulong _sequence; // 会话序号
    private ConfigurationSnapshot? _configSnapshot; // 配置快照
    private EnvironmentSnapshot? _environmentSnapshot; // 环境快照

    public CapabilityOrchestrator(
        ApplicationPaths paths,
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

    /// <summary>启动采集与 .strokebin 写入。</summary>
    public async Task<SessionInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        var session = await _sessionManager.CreateAsync(cancellationToken);
        await _sessionManager.TransitionAsync(SessionState.Initializing, cancellationToken);

        // .part 仅扫描报告，禁止默认当成完整会话改名
        var leftover = await _recoveryReader.RecoverPartFilesAsync(
            Path.Combine(_paths.StrokeRoot, "stroke"), cancellationToken);
        if (leftover > 0)
            Console.WriteLine($"发现 {leftover} 个未完整提交的 .strokebin.part（已保留，未改名）。");

        // 1) 加载笔配置
        var defaults = _profileProvider.GetDefaultProfile();
        var presets = _profileProvider.GetDevicePresets();
        var user = _profileProvider.TryLoadUserProfile();
        var configPresent = _profileProvider.HasDefaultConfigFile;

        // 2) 检测设备（无设备不致命）
        var detected = await _inputSource.DetectDevicesAsync(cancellationToken);
        var device = _inputSource.DetectedDevices.FirstOrDefault();
        var deviceName = device?.Name;
        var deviceId = device?.DeviceId ?? deviceName ?? "unknown";
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

        // 4) 启动 STRO 笔迹会话（旁路记录，不进 BRLOG）
        _penRecorder = new PenInputRecorder(_paths.StrokeRoot);
        await _penRecorder.RecordMetadataAsync("deviceName", deviceName ?? "unknown", cancellationToken);
        await _penRecorder.RecordMetadataAsync("deviceId", deviceId, cancellationToken);
        await _penRecorder.RecordMetadataAsync("profileId", profile.ProfileId, cancellationToken);
        _penRecorder.StartSession(deviceName ?? "unknown", deviceId);
        _recorderBus.Register(_penRecorder);
        _recorderBus.Register(new KeyboardContextRecorder());
        _recorderBus.Register(new BrushContextRecorder());
        _recorderBus.Register(new LayerContextRecorder());

        _normalizer = new InputEventNormalizer(profile);
        _inputSource.ReportReceived += OnReport;
        _inputSource.DeviceChanged += OnDeviceChanged;

        _busSubscription = _eventBus.Subscribe(async (evt, ct) =>
        {
            await _recorderBus.DispatchAsync(evt, ct); // 记录失败不得阻断后续
        });

        _eventBus.Publish(new InputEvent
        {
            Type = InputEventType.ConfigurationApplied,
            Timestamp = DateTimeOffset.UtcNow,
            SessionId = _penRecorder.CurrentSessionId,
            DeviceId = deviceName ?? "none",
            Sequence = Interlocked.Increment(ref _sequence),
            Message = $"Applied profile '{profile.DisplayName}' from {source}",
            ContactState = ContactState.OutOfRange
        });

        _eventBus.Publish(new InputEvent
        {
            Type = InputEventType.EnvironmentCapabilityChanged,
            Timestamp = DateTimeOffset.UtcNow,
            SessionId = _penRecorder.CurrentSessionId,
            DeviceId = "system",
            Sequence = Interlocked.Increment(ref _sequence),
            Message = $"vMulti={_environmentSnapshot.VMulti}; WinInk={_environmentSnapshot.WindowsInk}",
            ContactState = ContactState.OutOfRange
        });

        await _inputSource.StartAsync(cancellationToken);
        await _sessionManager.TransitionAsync(SessionState.Ready, cancellationToken);
        await _sessionManager.TransitionAsync(SessionState.Recording, cancellationToken);

        Console.WriteLine();
        Console.WriteLine("软件已就绪。正在持续采集数位板输入数据…");
        Console.WriteLine($"会话: {_penRecorder.CurrentSessionId}");
        Console.WriteLine($"输出: {_penRecorder.CurrentFilePath}");
        Console.WriteLine("按 Enter 停止。");

        return session;
    }

    /// <summary>停止采集并完整关闭 .strokebin 会话。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _inputSource.ReportReceived -= OnReport;
        _inputSource.DeviceChanged -= OnDeviceChanged;
        await _inputSource.StopAsync(cancellationToken);
        _busSubscription?.Dispose();
        await _recorderBus.DisposeAsync(); // 内含 StopSession / SessionEnd / 改名

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
        Console.WriteLine(env.TabletDevicePresent ? "检测到设备" : "未检测到设备（将继续等待）");
        Console.WriteLine($"当前配置已应用: {_configSnapshot!.AppliedProfile.DisplayName} ({_configSnapshot.Source})");
        Console.WriteLine($"Windows Ink: {DescribeInk(env.WindowsInk)}");
        Console.WriteLine($"vMulti: {DescribeVMulti(env.VMulti)}");

        foreach (var guide in env.Guides)
        {
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
        var sessionId = _penRecorder?.CurrentSessionId
            ?? _sessionManager.Current?.SessionId
            ?? "unknown";
        var seq = Interlocked.Increment(ref _sequence);
        foreach (var evt in _normalizer!.Normalize(report, sessionId, seq))
            _eventBus.Publish(evt);
    }

    private void OnDeviceChanged(object? sender, DetectedDeviceInfo device)
    {
        var sessionId = _penRecorder?.CurrentSessionId
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
