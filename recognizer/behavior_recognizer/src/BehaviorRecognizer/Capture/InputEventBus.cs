using System.Threading.Channels;
using BehaviorRecognizer.Abstractions.Input;

namespace BehaviorRecognizer.Capture;

/// <summary>
/// Non-blocking publish / fan-out bus. Capture threads must never wait on recorder IO.
/// </summary>
public sealed class InputEventBus : IInputEventBus
{
    private readonly Channel<InputEvent> _channel;
    private readonly List<Func<InputEvent, CancellationToken, ValueTask>> _handlers = [];
    private readonly object _handlerSync = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _dispatcher;
    private bool _disposed;

    public InputEventBus(int capacity = 8192)
    {
        _channel = Channel.CreateBounded<InputEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _dispatcher = Task.Run(DispatchLoopAsync);
    }

    public void Publish(InputEvent inputEvent)
    {
        if (_disposed)
            return;

        // Never block the capture thread.
        _channel.Writer.TryWrite(inputEvent);
    }

    public IDisposable Subscribe(Func<InputEvent, CancellationToken, ValueTask> handler)
    {
        lock (_handlerSync)
            _handlers.Add(handler);

        return new Subscription(() =>
        {
            lock (_handlerSync)
                _handlers.Remove(handler);
        });
    }

    public async IAsyncEnumerable<InputEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reader = Channel.CreateUnbounded<InputEvent>();
        using var sub = Subscribe(async (evt, ct) =>
        {
            await reader.Writer.WriteAsync(evt, ct);
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await foreach (var evt in reader.Reader.ReadAllAsync(linked.Token))
            yield return evt;
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                Func<InputEvent, CancellationToken, ValueTask>[] snapshot;
                lock (_handlerSync)
                    snapshot = _handlers.ToArray();

                foreach (var handler in snapshot)
                {
                    try
                    {
                        await handler(evt, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[EventBus] handler failure: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _channel.Writer.TryComplete();
        await _cts.CancelAsync();
        try
        {
            await _dispatcher;
        }
        catch
        {
            // ignored
        }

        _cts.Dispose();
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                unsubscribe();
        }
    }
}
