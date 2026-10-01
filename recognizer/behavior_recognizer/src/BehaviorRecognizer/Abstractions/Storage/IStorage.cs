using BehaviorRecognizer.Abstractions.Config;
using BehaviorRecognizer.Abstractions.Environment;
using BehaviorRecognizer.Abstractions.Input;

namespace BehaviorRecognizer.Abstractions.Storage;

public sealed class SessionHeader
{
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required int FormatVersion { get; init; }
    public required EnvironmentSnapshot Environment { get; init; }
    public required ConfigurationSnapshot Configuration { get; init; }
    public IReadOnlyList<DetectedDeviceInfo> Devices { get; init; } = [];
}

public interface IEventStore
{
    Task AppendAsync(InputEvent inputEvent, CancellationToken cancellationToken = default);

    Task FlushAsync(CancellationToken cancellationToken = default);
}

public interface ISessionWriter : IAsyncDisposable
{
    string SessionFilePath { get; }

    Task WriteHeaderAsync(SessionHeader header, CancellationToken cancellationToken = default);

    Task WriteEventAsync(InputEvent inputEvent, CancellationToken cancellationToken = default);

    Task WriteChunkAsync(string chunkType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    Task CompleteAsync(CancellationToken cancellationToken = default);
}

public interface IEventExporter
{
    Task ExportJsonAsync(string sessionFilePath, string outputPath, CancellationToken cancellationToken = default);
}

public interface IRecoveryReader
{
    Task<int> RecoverPartFilesAsync(string sessionsDirectory, CancellationToken cancellationToken = default);
}
