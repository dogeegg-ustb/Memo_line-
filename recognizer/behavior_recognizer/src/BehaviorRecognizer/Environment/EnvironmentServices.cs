using System.Runtime.Versioning;
using System.Security.Principal;
using BehaviorRecognizer.Abstractions.Environment;
using Microsoft.Win32;

namespace BehaviorRecognizer.Environment;

public sealed class VMultiDetector : IVMultiDetector
{
    public const string InstallGuideUrl = "https://github.com/X9VoiD/vmulti-bin/releases/latest";
    public const string DocumentationUrl = "https://github.com/X9VoiD/vmulti-bin";

    public VMultiStatus Detect()
    {
        if (!OperatingSystem.IsWindows())
            return VMultiStatus.Unknown;

        return DetectWindows();
    }

    [SupportedOSPlatform("windows")]
    private static VMultiStatus DetectWindows()
    {
        try
        {
            using var serviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\vmulti");
            if (serviceKey is null)
            {
                // Fallback: look for well-known virtual HID hardware IDs used by vMulti.
                if (DeviceEnumerator.HasDeviceMatching("vmulti") ||
                    DeviceEnumerator.HasDeviceMatching("VID_00FF&PID_0000"))
                {
                    return VMultiStatus.InstalledButInactive;
                }

                return VMultiStatus.NotInstalled;
            }

            var start = serviceKey.GetValue("Start");
            if (start is int startValue && startValue == 4)
                return VMultiStatus.InstalledButInactive;

            return VMultiStatus.Installed;
        }
        catch (UnauthorizedAccessException)
        {
            return VMultiStatus.PermissionDenied;
        }
        catch
        {
            return VMultiStatus.Unknown;
        }
    }

    public CapabilityGuide CreateInstallGuide() => new()
    {
        Title = "缺少 vMulti",
        Message =
            "检测到系统尚未安装 vMulti 虚拟 HID 驱动。基础数位板采集仍可继续；" +
            "若需要 Windows Ink / 压感输出到绘图软件，请按引导安装 vMulti，然后重新检测。",
        DocumentationUrl = DocumentationUrl,
        InstallerUrl = InstallGuideUrl,
        BlocksBasicCapture = false
    };
}

[SupportedOSPlatform("windows")]
internal static class DeviceEnumerator
{
    public static bool HasDeviceMatching(string token)
    {
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID");
            if (enumKey is null)
                return false;

            foreach (var subName in enumKey.GetSubKeyNames())
            {
                if (subName.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Ignore enumeration failures; caller treats as unknown/not installed.
        }

        return false;
    }
}

public sealed class WindowsInkProbe : IWindowsInkProbe
{
    public WindowsInkStatus Probe()
    {
        if (!OperatingSystem.IsWindows())
            return WindowsInkStatus.NotApplicable;

        try
        {
            // Windows Ink requires Windows 10+. RealTimeStylus / ink stack lives in the OS.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10))
                return WindowsInkStatus.Unavailable;

            // Presence of ink runtime assemblies is a good signal without hard-failing capture.
            var inkAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name?.Contains("Windows.UI.Input.Inking", StringComparison.OrdinalIgnoreCase) == true);

            if (inkAssembly)
                return WindowsInkStatus.Available;

            // Registry hint for pen & touch.
            using var tabletPc = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\TabletTip");
            using var ink = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsInkWorkspace");
            if (tabletPc is not null || ink is not null)
                return WindowsInkStatus.Available;

            // On modern Windows 10/11 assume OS Ink APIs exist even if registry keys vary.
            return WindowsInkStatus.Available;
        }
        catch
        {
            return WindowsInkStatus.Unknown;
        }
    }

    public CapabilityGuide? CreateGuide(WindowsInkStatus status)
    {
        if (status is WindowsInkStatus.Available or WindowsInkStatus.NotApplicable)
            return null;

        return new CapabilityGuide
        {
            Title = "Windows Ink 不可用",
            Message =
                "当前环境未确认 Windows Ink 能力。基础采集不受影响；" +
                "如需 Ink 输出，请确认系统为 Windows 10/11 并安装相关可选组件后重试。",
            DocumentationUrl = "https://support.microsoft.com/windows",
            BlocksBasicCapture = false
        };
    }
}

public sealed class EnvironmentProbe : IEnvironmentProbe
{
    private readonly IVMultiDetector _vMulti;
    private readonly IWindowsInkProbe _windowsInk;

    public EnvironmentProbe(IVMultiDetector vMulti, IWindowsInkProbe windowsInk)
    {
        _vMulti = vMulti;
        _windowsInk = windowsInk;
    }

    public EnvironmentSnapshot Probe(bool tabletDevicePresent, bool defaultConfigPresent)
    {
        var isWindows = OperatingSystem.IsWindows();
        var elevated = IsElevated();
        var vMulti = isWindows ? _vMulti.Detect() : VMultiStatus.Unknown;
        var ink = _windowsInk.Probe();

        var guides = new List<CapabilityGuide>();
        if (vMulti is VMultiStatus.NotInstalled or VMultiStatus.InstalledButInactive or VMultiStatus.PermissionDenied)
            guides.Add(_vMulti.CreateInstallGuide());

        var inkGuide = _windowsInk.CreateGuide(ink);
        if (inkGuide is not null)
            guides.Add(inkGuide);

        if (!tabletDevicePresent)
        {
            guides.Add(new CapabilityGuide
            {
                Title = "未检测到数位板",
                Message = "目前没有可用的数位板输入设备。请连接设备后重新扫描；软件会保持就绪并继续等待。",
                BlocksBasicCapture = false
            });
        }

        if (!defaultConfigPresent)
        {
            guides.Add(new CapabilityGuide
            {
                Title = "默认配置缺失",
                Message = "内置默认笔配置不可用，已回退到程序内置常量配置以保证采集不中断。",
                BlocksBasicCapture = false
            });
        }

        return new EnvironmentSnapshot
        {
            IsWindows = isWindows,
            HasElevatedPrivileges = elevated,
            VMulti = vMulti,
            WindowsInk = ink,
            TabletDevicePresent = tabletDevicePresent,
            DefaultConfigPresent = defaultConfigPresent,
            Guides = guides,
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        return IsElevatedWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevatedWindows()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
