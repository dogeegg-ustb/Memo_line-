using BehaviorRecognizer.Abstractions.Config;
using BehaviorRecognizer.Abstractions.Environment;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Abstractions.Storage;
using BehaviorRecognizer.Config;
using BehaviorRecognizer.Storage;
using Xunit;

namespace BehaviorRecognizer.Tests;

public class StorageTests
{
    [Fact]
    public async Task Write_And_Export_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "br-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var sessionId = Guid.NewGuid().ToString("N");
            await using var writer = new BrlogSessionWriter(dir, sessionId);

            var profile = PenProfileProvider.CreateHardcodedDefault();
            await writer.WriteHeaderAsync(new SessionHeader
            {
                SessionId = sessionId,
                StartedAt = DateTimeOffset.UtcNow,
                FormatVersion = BrlogSessionWriter.FormatVersion,
                Environment = new EnvironmentSnapshot
                {
                    IsWindows = true,
                    HasElevatedPrivileges = false,
                    VMulti = VMultiStatus.Unknown,
                    WindowsInk = WindowsInkStatus.Available,
                    TabletDevicePresent = true,
                    DefaultConfigPresent = true,
                    Guides = [],
                    CapturedAt = DateTimeOffset.UtcNow
                },
                Configuration = new ConfigurationSnapshot
                {
                    SnapshotId = "snap",
                    CreatedAt = DateTimeOffset.UtcNow,
                    AppliedProfile = profile,
                    Source = "test"
                }
            });

            await writer.WriteEventAsync(new InputEvent
            {
                Type = InputEventType.PenMove,
                Timestamp = DateTimeOffset.UtcNow,
                SessionId = sessionId,
                DeviceId = "dev",
                Sequence = 1,
                ContactState = ContactState.Contact,
                Pressure = 0.5f
            });

            await writer.CompleteAsync();

            var brlog = writer.SessionFilePath;
            Assert.True(File.Exists(brlog));

            var jsonl = Path.Combine(dir, "out.jsonl");
            await new JsonEventExporter().ExportJsonAsync(brlog, jsonl);
            var text = await File.ReadAllTextAsync(jsonl);
            Assert.Contains("penMove", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
