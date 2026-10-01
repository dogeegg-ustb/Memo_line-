using BehaviorRecognizer.Abstractions.Input;

namespace BehaviorRecognizer.Abstractions.Recording;

public interface IRecorder : IAsyncDisposable
{
    string Id { get; }

    string DisplayName { get; }

    bool IsEnabled { get; set; }

    ValueTask OnEventAsync(InputEvent inputEvent, CancellationToken cancellationToken = default);
}

public interface IRecorderPlugin : IRecorder
{
    string PluginKind { get; }
}

public interface IMetadataRecorder : IRecorder
{
    ValueTask RecordMetadataAsync(string key, object? value, CancellationToken cancellationToken = default);
}

public interface IContextRecorder : IRecorder
{
    /// <summary>Context domain such as keyboard, brush, layer, external.</summary>
    string ContextDomain { get; }
}

public interface IRecorderBus : IAsyncDisposable
{
    IReadOnlyList<IRecorder> Recorders { get; }

    void Register(IRecorder recorder);

    bool SetEnabled(string recorderId, bool enabled);

    ValueTask DispatchAsync(InputEvent inputEvent, CancellationToken cancellationToken = default);
}
