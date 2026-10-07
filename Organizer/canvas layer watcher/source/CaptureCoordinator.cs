namespace CanvasLayerWatcher;

internal sealed class CaptureCoordinator : IDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    public async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        await _serial.WaitAsync(token);
        return new Lease(_serial);
    }
    internal Task WaitAsync(CancellationToken token) => _serial.WaitAsync(token);
    internal void Release() => _serial.Release();
    public void Dispose() => _serial.Dispose();
    private sealed class Lease(SemaphoreSlim serial) : IDisposable
    {
        private SemaphoreSlim? _owner = serial;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
