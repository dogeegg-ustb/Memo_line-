namespace BehaviorRecognizer.Abstractions.Input;

public interface IInputEventNormalizer
{
    IEnumerable<InputEvent> Normalize(RawInputReport report, string sessionId, ulong sequence);
}

public interface IInputEventBus : IAsyncDisposable
{
    void Publish(InputEvent inputEvent);

    IDisposable Subscribe(Func<InputEvent, CancellationToken, ValueTask> handler);

    IAsyncEnumerable<InputEvent> SubscribeAsync(CancellationToken cancellationToken = default);
}
