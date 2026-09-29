namespace BehaviorRecognizer.Abstractions.Environment;

public interface IVMultiDetector
{
    VMultiStatus Detect();

    CapabilityGuide CreateInstallGuide();
}

public interface IWindowsInkProbe
{
    WindowsInkStatus Probe();

    CapabilityGuide? CreateGuide(WindowsInkStatus status);
}

public interface IEnvironmentProbe
{
    EnvironmentSnapshot Probe(bool tabletDevicePresent, bool defaultConfigPresent);
}
