using BehaviorRecognizer.Abstractions.Config;
using BehaviorRecognizer.Abstractions.Environment;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Abstractions.Recording;
using BehaviorRecognizer.Abstractions.Session;
using BehaviorRecognizer.Abstractions.Storage;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Config;
using BehaviorRecognizer.Environment;
using BehaviorRecognizer.Recording;
using BehaviorRecognizer.Session;
using BehaviorRecognizer.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace BehaviorRecognizer.Bootstrap;

public static class ServiceRegistration
{
    public static IServiceCollection AddBehaviorRecognizer(this IServiceCollection services, ApplicationPaths paths)
    {
        services.AddSingleton(paths);

        services.AddSingleton<IVMultiDetector, VMultiDetector>();
        services.AddSingleton<IWindowsInkProbe, WindowsInkProbe>();
        services.AddSingleton<IEnvironmentProbe, EnvironmentProbe>();

        services.AddSingleton<IPenProfileProvider>(_ => new PenProfileProvider(paths.Config));
        services.AddSingleton<IDeviceProfileMatcher, DeviceProfileMatcher>();
        services.AddSingleton<IConfigurationSnapshotProvider, ConfigurationSnapshotProvider>();

        services.AddSingleton<IInputSource, OtdInputSource>();
        services.AddSingleton<IInputEventBus, InputEventBus>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<IRecoveryReader, RecoveryReader>();
        services.AddSingleton<IEventExporter, JsonEventExporter>();

        services.AddSingleton<IRecorderBus, RecorderBus>();
        services.AddSingleton<CapabilityOrchestrator>();

        return services;
    }
}
