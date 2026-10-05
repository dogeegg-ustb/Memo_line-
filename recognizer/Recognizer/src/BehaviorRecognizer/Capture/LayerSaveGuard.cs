using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Storage.Memoline;

namespace BehaviorRecognizer.Capture;

/// <summary>Hook callbacks only buffer. A dedicated worker dispatches save in-process
/// (or through AHK for automatic layer protection), then replays original input in order.</summary>
public sealed class LayerSaveGuard : IAsyncDisposable, IRecorderInputControl
{
    private sealed record Configuration(bool Enabled, int[] Region, HashSet<(int,int)> Keys,
        string Executable, long Updated, bool Controlled = false);
    private readonly MemolineWriter _writer;
    private readonly Action<object> _notify;
    private readonly Action<Process>? _registerProcess;
    private readonly ILayerSaveGuardBackend? _backend;
    private readonly object _sync = new();
    private sealed record Buffered(Input Input, bool SaveFirst, long Ticks, int Modifiers, bool ExactReplay);
    private sealed record SaveDiagnostic(Buffered Original, bool Controlled, string SaveId,
        bool Released, long ReleasedTicks, long? DispatchedTicks, string? Error, RecorderClipSaveRequest? ExternalRequest = null);
    private sealed record OneShotSave(RecorderClipSaveRequest Request, long Ticks, CancellationToken Cancellation,
        TaskCompletionSource<RecorderClipSaveResult> Completion);
    private OneShotSave? _oneShot;
    private readonly Channel<SaveDiagnostic> _diagnostics = Channel.CreateUnbounded<SaveDiagnostic>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Task _diagnosticWorker;
    private readonly HashSet<int> _blockedKeys = [];
    private uint _blockedMouseButtons;
    private volatile bool _workerReady;
    private volatile bool _directReady;
    private readonly List<Buffered> _pending = [];
    private Configuration _config = new(false, [], [], "", 0);
    private Configuration? _controlConfig;
    private readonly SemaphoreSlim _controlLock = new(1, 1);
    private readonly SemaphoreSlim _workAvailable = new(0, 1);
    private Configuration? _activeConfig;
    private nint _activeWindow;
    private bool _busy, _closed;
    private readonly Task _worker;
    private readonly TaskCompletionSource _workerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly ushort[] ModifierKeys = [0xA2,0xA3,0xA0,0xA1,0xA4,0xA5,0x5B,0x5C];
    private Process? _ahk;
    private readonly SemaphoreSlim _ahkLock = new(1,1);
    private Task _warmup = Task.CompletedTask;
    private bool _warmupStarted;
    private string? _reportedError;

    public LayerSaveGuard(MemolineWriter writer, Action<object> notify, Action<Process>? registerProcess=null)
        : this(writer, notify, registerProcess, null) { }

    internal LayerSaveGuard(MemolineWriter writer, Action<object> notify, Action<Process>? registerProcess, ILayerSaveGuardBackend? backend)
    {
        (_writer,_notify,_registerProcess) = (writer,notify,registerProcess);
        _backend = backend;
        _diagnosticWorker = Task.Run(WriteDiagnosticsAsync);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker = completion.Task;
        var thread = new Thread(() =>
        {
            try { RunWorker(); completion.TrySetResult(); }
            catch (Exception ex) { _workerStarted.TrySetException(ex); completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "CSP save-before-input", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private Configuration EffectiveConfiguration => Volatile.Read(ref _controlConfig) ?? Volatile.Read(ref _config);
    private bool SaveWorkerReady => _workerReady && (_backend?.SaveWorkerReady ?? (_ahk is { HasExited: false }));
    private bool ReadyFor(Configuration config) => config.Controlled
        ? _directReady && !_worker.IsCompleted && (_backend?.SaveWorkerReady ?? true) : SaveWorkerReady;
    private nint ForegroundWindow => _backend?.ForegroundWindow ?? Native.GetForegroundWindow();

    public RecorderInputControlStatus GetInputControlStatus()
    {
        lock (_sync)
        {
            var control = Volatile.Read(ref _controlConfig);
            return new(true, control is not null, !_closed && (control ?? _config).Enabled,
                ReadyFor(control ?? _config), _busy, null);
        }
    }

    public async Task<RecorderInputControlStatus> ConfigureInputInterceptionAsync(
        RecorderInputControlRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _controlLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            string exe = Volatile.Read(ref _config).Executable;
            var config = new Configuration(request.Enabled, [], [], exe, Stopwatch.GetTimestamp(), Controlled: true);
            if (request.Enabled)
            {
                if (_backend is null && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                // Prepare the resident in-process path before exposing the enabled policy.
                await _workerStarted.Task.WaitAsync(cancellationToken);
                if (_backend is not null) await _backend.InitializeSaveWorkerAsync("inProcess");
                if (_backend?.SaveWorkerReady == false) throw new IOException("Save dispatcher did not become ready.");
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_closed, this);
                _directReady = true;
            }
            Volatile.Write(ref _controlConfig, config);
            var status = GetInputControlStatus();
            _writer.AppendState("inputControlChanged", _writer.NowTicks, [], status, "immediate");
            return status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GetInputControlStatus() with { Success = false, Error = ex.Message };
        }
        finally { _controlLock.Release(); }
    }

    public RecorderInputControlStatus RestoreAutomaticInputProtection()
    {
        // Serialize with asynchronous configuration so a finishing warmup cannot
        // re-arm controlled interception after the caller restored automatic policy.
        _controlLock.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            Volatile.Write(ref _controlConfig, null);
            var status = GetInputControlStatus();
            _writer.AppendState("inputControlChanged", _writer.NowTicks, [], status, "immediate");
            return status;
        }
        finally { _controlLock.Release(); }
    }

    public async Task<RecorderClipSaveResult> RequestClipSaveAsync(RecorderClipSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Path.IsPathFullyQualified(request.ExpectedClipPath) || !request.ExpectedClipPath.EndsWith(".clip", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
            throw new ArgumentException("Expected an absolute .clip path and valid requestId.");
        if (request.TriggerTicks is < 0) throw new ArgumentException("triggerTicks must be nonnegative.");
        if (request.TriggerTicks > _writer.NowTicks)
            throw new ArgumentException("triggerTicks must not be later than this recording session's current ticks.");
        await _workerStarted.Task.WaitAsync(cancellationToken);
        var completion = new TaskCompletionSource<RecorderClipSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_busy) return new(false, request.RequestId, false, null, false, "Save worker is busy; retry after it drains.", request.TriggerTicks);
            cancellationToken.ThrowIfCancellationRequested();
            _busy = true;
            _activeWindow = ForegroundWindow;
            _activeConfig = EffectiveConfiguration;
            _oneShot = new(request, _writer.NowTicks, cancellationToken, completion);
            _workAvailable.Release();
        }
        // Once queued, cancellation is checked by the resident worker before dispatch.
        // The response always distinguishes a dispatched chord from an unexecuted request.
        return await completion.Task;
    }

    private async Task DispatchOneShotAsync(OneShotSave save, nint hwnd)
    {
        string? error = null; long? dispatchedTicks = null;
        try
        {
            save.Cancellation.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!(_backend?.IsCspForeground ?? CspWindowProbe.IsCspForeground()) || ForegroundWindow != hwnd)
                throw new IOException("The requested CSP document is not foreground.");
            if (_backend is not null)
            {
                await _backend.InitializeSaveWorkerAsync("inProcess");
                save.Cancellation.ThrowIfCancellationRequested();
                if (!_backend.SaveWorkerReady || ForegroundWindow != hwnd) throw new IOException("Save dispatcher is unavailable or CSP lost foreground.");
                await _backend.DispatchSaveAsync(hwnd);
            }
            else DispatchSaveInProcess(hwnd);
            dispatchedTicks = _writer.NowTicks;
        }
        catch (Exception ex) { error = ex.Message; }
        _diagnostics.Writer.TryWrite(new(new(default, false, save.Ticks, 0, false), true,
            save.Request.RequestId, false, _writer.NowTicks, dispatchedTicks, error, save.Request));
        save.Completion.TrySetResult(new(error is null, save.Request.RequestId, dispatchedTicks is not null,
            dispatchedTicks, false, error, save.Request.TriggerTicks));
    }

    private static string ResolveExecutable(string? exe)
        => !string.IsNullOrWhiteSpace(exe) ? exe : new[] {
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),"Programs","AutoHotkey","v2","AutoHotkey64.exe"),
            @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe" }.FirstOrDefault(File.Exists) ?? "";

    public void Configure(JsonElement data)
    {
        string exe = ResolveExecutable(data.GetProperty("autoHotkeyPath").GetString());
        bool enabled = data.GetProperty("enabled").GetBoolean();
        if (enabled && !File.Exists(exe))
        {
            if (_reportedError != "missing")
                _writer.AppendState("saveGuardUnavailable",_writer.NowTicks,[],new { message="AutoHotkey v2 not found; configure autoHotkeyPath. Layer operations are not intercepted." },"immediate");
            _reportedError = "missing";
            enabled = false;
        }
        var region = data.GetProperty("region");
        var config = new Configuration(enabled, region.ValueKind == JsonValueKind.Array ? region.EnumerateArray().Select(v=>v.GetInt32()).ToArray() : [],
            data.GetProperty("bindings").EnumerateArray().Select(v=>(v.GetProperty("vk").GetInt32(),v.GetProperty("mods").GetInt32())).ToHashSet(),
            exe,Stopwatch.GetTimestamp());
        Volatile.Write(ref _config,config);
        lock (_sync)
        {
            if (enabled && !_closed && !_warmupStarted)
            {
                _warmupStarted=true;
                _warmup=Task.Run(async () =>
                {
                    try { await EnsureSaveWorkerAsync(config); }
                    catch (Exception ex) {
                        Console.Error.WriteLine($"[图层保存保护未启用] {ex.Message}；输入保持放行。");
                        _writer.AppendState("saveGuardUnavailable",_writer.NowTicks,[],new { message=ex.Message, inputPolicy="passThrough" },"immediate");
                    }
                });
            }
        }
    }

    private bool Available(Configuration config) => !_closed && config.Enabled &&
        (config.Controlled || (config.Region.Length == 4 && Stopwatch.GetTimestamp()-config.Updated < Stopwatch.Frequency*2)) &&
        ReadyFor(config) && (_backend?.IsCspForeground ?? CspWindowProbe.IsCspForeground());

    public bool Keyboard(int vk, uint scan, bool extended, bool down)
    {
        long ticks = _writer.NowTicks;
        // Automatic layer protection leaves modifiers alone; explicit Windows input control buffers them too.
        var config = EffectiveConfiguration;
        lock (_sync)
            if (!config.Controlled && !_blockedKeys.Contains(vk) && !config.Keys.Any(binding=>binding.Item1==vk)) return false;
        bool modifier = vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;
        int mods = (Held(17)?4:0)|(Held(16)?2:0)|(Held(18)?1:0);
        bool available = Available(config);
        lock (_sync)
        {
            bool trigger = config.Controlled ? available && !_busy
                : !modifier && down && !Held(91) && !Held(92) && config.Keys.Contains((vk,mods)) && available;
            if (!trigger && !(config.Controlled && available && _busy) && !_blockedKeys.Contains(vk)) return false;
            if (down) _blockedKeys.Add(vk);
            var input = new Input { Type=1, Union=new() { Keyboard=new() { Vk=(ushort)vk, Scan=(ushort)scan,
                Flags=(extended?1u:0u)|(down?0u:2u), Extra=WindowsInputHooks.RecorderInputTag } } };
            return Buffer(input,trigger,config,mods,ticks);
        }
    }

    private static uint MouseButton(int message,uint data) => message switch {
        0x201 or 0x202=>1, 0x204 or 0x205=>2, 0x207 or 0x208=>4,
        0x20B or 0x20C=>(data>>16)==1?8u:16u, _=>0 };

    public bool Mouse(int message, int x, int y, uint data, bool penCompatibility = false)
    {
        long ticks = _writer.NowTicks;
        bool click = message is 0x201 or 0x204 or 0x207 or 0x20B;
        var config = EffectiveConfiguration;
        if (config.Controlled && penCompatibility) return false;
        bool available = Available(config) && (_backend?.IsCspPoint(x,y) ?? CspWindowProbe.IsCspPoint(x,y));
        uint button=MouseButton(message,data);
        lock (_sync)
        {
            bool trigger = config.Controlled ? available && !_busy
                : click && available && x>=config.Region[0] && y>=config.Region[1] &&
                    x<config.Region[0]+config.Region[2] && y<config.Region[1]+config.Region[3];
            // Automatic policy only holds a guarded button's continuation.
            if (!trigger && !(config.Controlled && available && _busy) && !(message==0x200 && _blockedMouseButtons!=0) && (_blockedMouseButtons&button)==0) return false;
            uint flags = message switch { 0x200=>1,0x201=>2,0x202=>4,0x204=>8,0x205=>16,
                0x207=>32,0x208=>64,0x20B=>128,0x20C=>256,0x20A=>0x800,0x20E=>0x1000,_=>0 };
            if (flags==0) return false;
            if (click) _blockedMouseButtons|=button;
            int left=GetSystemMetric(76), top=GetSystemMetric(77);
            int width=GetSystemMetric(78), height=GetSystemMetric(79);
            var input = new Input { Type=0, Union=new() { Mouse=new() {
                X=(int)((long)(x-left)*65535/Math.Max(1,width-1)), Y=(int)((long)(y-top)*65535/Math.Max(1,height-1)),
                Data=message is 0x20B or 0x20C ? data>>16
                    : message is 0x20A or 0x20E ? unchecked((uint)(int)(short)(data>>16)) : 0,
                Flags=flags|1|0x8000|0x4000, Extra=WindowsInputHooks.RecorderInputTag } } };
            return Buffer(input,trigger,config,0,ticks);
        }
    }

    private bool Buffer(Input input, bool trigger, Configuration config, int modifiers, long ticks)
    {
        lock (_sync)
        {
            if (_closed || (!_busy && !trigger)) return false;
            _pending.Add(new(input,trigger,ticks,modifiers,config.Controlled));
            if (!_busy)
            {
                _busy=true;
                _activeWindow = ForegroundWindow;
                _activeConfig = config;
                _workAvailable.Release();
            }
            return true;
        }
    }

    private void RunWorker()
    {
        _workerStarted.TrySetResult();
        while (true)
        {
            _workAvailable.Wait();
            Configuration? config;
            nint hwnd;
            OneShotSave? oneShot;
            lock (_sync)
            {
                if (_closed && !_busy) return;
                config = _activeConfig;
                hwnd = _activeWindow;
                oneShot = _oneShot; _oneShot = null;
            }
            if (oneShot is not null) DispatchOneShotAsync(oneShot, hwnd).GetAwaiter().GetResult();
            if (config is not null) SaveAndReleaseAsync(config, hwnd).GetAwaiter().GetResult();
            lock (_sync) if (_closed && !_busy) return;
        }
    }

    private static Input KeyInput(ushort vk,bool down) => new() { Type=1, Union=new() { Keyboard=new() {
        Vk=vk, Flags=(down?0u:2u)|(vk is 0xA3 or 0xA5 or 0x5B or 0x5C?1u:0u), Extra=WindowsInputHooks.RecorderInputTag } } };

    private IEnumerable<Input> ReplayInputs(Buffered item)
    {
        if (item.ExactReplay || !item.SaveFirst || item.Input.Type!=1) { yield return item.Input; yield break; }
        // Modifier key-ups are never swallowed. Restore the original shortcut's chord
        // locally during replay, then return to the user's current modifier state.
        ushort[] modifiers=ModifierKeys;
        var held=modifiers.Where(v=>Held(v)).ToHashSet();
        int Mask(ushort key) => key is 0xA2 or 0xA3?4:key is 0xA0 or 0xA1?2:key is 0xA4 or 0xA5?1:0;
        var released=held.Where(v=>Mask(v)==0||(item.Modifiers&Mask(v))==0).ToArray();
        var added=new List<ushort>();
        foreach (ushort key in released) yield return KeyInput(key,false);
        foreach (ushort key in new ushort[]{0xA2,0xA0,0xA4})
            if ((item.Modifiers&Mask(key))!=0 && !held.Any(v=>Mask(v)==Mask(key))) {
                added.Add(key); yield return KeyInput(key,true);
            }
        yield return item.Input;
        foreach (ushort key in added) yield return KeyInput(key,false);
        foreach (ushort key in released) yield return KeyInput(key,true);
    }

    private async Task SaveAndReleaseAsync(Configuration config, nint hwnd)
    {
        while (true)
        {
            Buffered[] batch;
            lock (_sync)
            {
                if (_pending.Count==0) { _busy=false; _blockedKeys.Clear(); _blockedMouseButtons=0; return; }
                int next=_pending.FindIndex(1,v=>v.SaveFirst);
                int count=next<0?_pending.Count:next;
                batch=_pending.Take(count).ToArray();
                _pending.RemoveRange(0,count);
            }
            bool save=batch[0].SaveFirst;
            long ticks=batch[0].Ticks;
            string? saveId=save?Guid.NewGuid().ToString("N"):null;
            string? error=null;
            long? dispatchedTicks=null;
            if (save)
            {
                _notify(new { type="guardStarted",ticks,saveId });
                try
                {
                    if (!ReadyFor(config))
                        throw new IOException("Save worker unavailable; original input will be released.");
                    if (_backend is not null)
                        await _backend.DispatchSaveAsync(hwnd);
                    else if (config.Controlled)
                        DispatchSaveInProcess(hwnd);
                    else
                    {
                        await _ahk!.StandardInput.WriteLineAsync($"{hwnd.ToInt64()}");
                        await _ahk.StandardInput.FlushAsync();
                        string? response=await _ahk.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(250));
                        if (response?.TrimStart('﻿') != "save-input-sent") throw new IOException(response ?? "AHK worker exited.");
                    }
                    dispatchedTicks=_writer.NowTicks;
                }
                catch(Exception ex)
                {
                    if (config.Controlled) _directReady=false;
                    else _workerReady=false;
                    error=ex.Message;
                    try { if (!config.Controlled && _ahk is { HasExited:false }) _ahk.Kill(); }
                    catch (Exception cleanupError) { error += "; cleanup: "+cleanupError.Message; }
                }
            }
            bool released=false;
            // Never hold _sync across SendInput: the hook thread may need it to
            // handle a real input while Windows is dispatching our replay.
            if (ForegroundWindow==hwnd)
            {
                var items=batch.SelectMany(ReplayInputs).ToArray();
                released=(_backend?.ReplayInputs(items) ?? Native.SendInput((uint)items.Length,items,Marshal.SizeOf<Input>()))==items.Length;
                if (!released) error=(error is null?"":error+"; ")+"SendInput did not replay every buffered input.";
            }
            else error=(error is null?"":error+"; ")+"CSP lost foreground; guarded input cancelled.";
            long releasedTicks=_writer.NowTicks;
            lock (_sync)
            {
                _blockedKeys.RemoveWhere(v=>!_pending.Any(i=>i.Input.Type==1&&i.Input.Union.Keyboard.Vk==v));
                if (!_pending.Any(i=>i.Input.Type==0)) _blockedMouseButtons=0;
            }
            if (save)
            {
                _notify(new { type="guardReleased",ticks=releasedTicks,saveTicks=ticks,saveId,released,error });
                // A separate queue keeps serialization, console output and writer contention
                // from delaying the next batch of hardware input too.
                _diagnostics.Writer.TryWrite(new(batch[0],config.Controlled,saveId!,released,releasedTicks,dispatchedTicks,error));
            }
        }
    }

    private async Task WriteDiagnosticsAsync()
    {
        await foreach (var result in _diagnostics.Reader.ReadAllAsync())
        {
            if (result.ExternalRequest is { } external)
            {
                long triggerTicks = external.TriggerTicks ?? result.Original.Ticks;
                _writer.AppendState("clipSaveRequest", triggerTicks, [], new {
                    saveId = external.RequestId, requestId = external.RequestId, reason = "externalControl",
                    triggerTicks, requestReceivedTicks = result.Original.Ticks,
                    expectedClipPath = external.ExpectedClipPath, confirmation = "requestOnly" }, "immediate");
                _writer.AppendState("saveGuardResult", triggerTicks, [], new {
                    saveId = external.RequestId, requestId = external.RequestId, result.Error,
                    triggerTicks, requestReceivedTicks = result.Original.Ticks,
                    saveInputDispatchedTicks = result.DispatchedTicks, saveCompletionConfirmed = false,
                    cspAcceptanceConfirmed = false }, "immediate");
                continue;
            }
            var original = result.Original;
            long ticks = original.Ticks;
            _writer.AppendState("clipSaveRequest",ticks,[],new { saveId=result.SaveId,
                reason=result.Controlled?"beforeControlledInput":"beforeLayerOperation", confirmation="requestOnly",
                releasePolicy="afterSaveInputDispatch", interceptedInput=new { type=original.Input.Type,
                    vk=original.Input.Type==1?(int?)original.Input.Union.Keyboard.Vk:null,modifiers=original.Modifiers } },"immediate");
            string inputLabel = original.Input.Type==1 ? $"vk={original.Input.Union.Keyboard.Vk}" : "鼠标／滚轮";
            Console.WriteLine($"[输入保存保护] {inputLabel} 保存已派发={result.DispatchedTicks is not null} 原指令已释放={result.Released} {result.Error}");
            _writer.AppendState("saveGuardResult",ticks,[],new { saveId=result.SaveId,
                released=result.Released,releasedTicks=result.ReleasedTicks,error=result.Error,
                saveInputDispatchedTicks=result.DispatchedTicks, originalInputPolicy="releaseOnSaveFailureWhileCspForeground",
                interceptionDurationMs=(result.ReleasedTicks-ticks)*1000.0/Stopwatch.Frequency,
                saveDispatchToReleaseMs=result.DispatchedTicks is { } dispatch ? (result.ReleasedTicks-dispatch)*1000.0/Stopwatch.Frequency : (double?)null,
                cspAcceptanceConfirmed=false, saveCompletionConfirmed=false },"immediate");
        }
    }

    private void DispatchSaveInProcess(nint hwnd)
    {
        if (ForegroundWindow != hwnd) throw new IOException("CSP lost foreground before save dispatch.");
        var (inputs, count) = CreateSaveInputs();
        uint sent = Native.SendInput((uint)count, inputs, Marshal.SizeOf<Input>());
        if (sent != count)
        {
            if (sent > 0 && ForegroundWindow == hwnd)
            {
                int heldCount = (count - 4) / 2;
                var recovery = new Input[heldCount + 2];
                recovery[0] = KeyInput(0x53, false);
                recovery[1] = KeyInput(0xA2, false);
                for (int i = 0; i < heldCount; ++i)
                    recovery[i + 2] = KeyInput(inputs[i].Union.Keyboard.Vk, true);
                Native.SendInput((uint)recovery.Length, recovery, Marshal.SizeOf<Input>());
            }
            throw new IOException("SendInput did not dispatch the complete save chord.");
        }
    }

    internal (Input[] Inputs, int Count) CreateSaveInputs()
    {
        // Same tagged Ctrl+S chord as the original AHK worker, with no IPC round-trip.
        // Temporarily release held modifiers, then restore their pre-save state.
        Span<ushort> held = stackalloc ushort[ModifierKeys.Length];
        int heldCount = 0;
        var inputs = new Input[ModifierKeys.Length * 2 + 4];
        int count = 0;
        foreach (ushort modifier in ModifierKeys)
            if (Held(modifier))
            {
                held[heldCount++] = modifier;
                inputs[count++] = KeyInput(modifier, false);
            }
        inputs[count++] = KeyInput(0xA2, true);
        inputs[count++] = KeyInput(0x53, true);
        inputs[count++] = KeyInput(0x53, false);
        inputs[count++] = KeyInput(0xA2, false);
        for (int i = 0; i < heldCount; ++i) inputs[count++] = KeyInput(held[i], true);
        return (inputs, count);
    }

    private async Task EnsureSaveWorkerAsync(Configuration config)
    {
        await _ahkLock.WaitAsync();
        string stage="processLaunch";
        try
        {
            if (_backend is not null)
            {
                await _backend.InitializeSaveWorkerAsync(config.Executable);
                _workerReady = _backend.SaveWorkerReady;
                if (!_workerReady) throw new IOException("Save worker did not become ready.");
                return;
            }
            if (_workerReady && _ahk is { HasExited:false }) return;
            _ahk?.Dispose();
            var start = new ProcessStartInfo(config.Executable) { UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
            start.ArgumentList.Add("/ErrorStdOut");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"integration","CSP_SaveBeforeClick.ahk"));
            start.ArgumentList.Add("--recorder-worker");
            _ahk=Process.Start(start) ?? throw new IOException("AHK save worker failed to start.");
            stage="processLifetimeRegistration";
            _registerProcess?.Invoke(_ahk);
            stage="readyHandshake";
            string? ready=await _ahk.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (ready?.TrimStart('﻿') != "ready") throw new IOException(ready ?? "AHK worker exited during initialization.");
            _workerReady=true;
        }
        catch (Exception ex)
        {
            _workerReady=false;
            string cleanup="";
            try { if (_ahk is { HasExited:false }) _ahk.Kill(); }
            catch (Exception cleanupError) { cleanup="; cleanup: "+cleanupError.Message; }
            throw new IOException($"AHK {stage}: {ex.Message}{cleanup}",ex);
        }
        finally { _ahkLock.Release(); }
    }

    private bool Held(int vk) => _backend?.IsKeyHeld(vk) ?? ((Native.GetAsyncKeyState(vk)&0x8000)!=0);
    private int GetSystemMetric(int metric) => _backend?.GetSystemMetric(metric) ?? Native.GetSystemMetrics(metric);
    public async ValueTask DisposeAsync()
    {
        lock (_sync) { _closed=true; if (_workAvailable.CurrentCount==0) _workAvailable.Release(); }
        await _warmup;
        await _worker;
        _diagnostics.Writer.TryComplete();
        await _diagnosticWorker;
        if (_ahk is { HasExited:false }) { _ahk.StandardInput.Close(); await _ahk.WaitForExitAsync(); }
        _ahk?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion {
        [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X,Y; public uint Data,Flags,Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Vk,Scan; public uint Flags,Time; public nuint Extra; }
    private static class Native {
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int metric);
        [DllImport("user32.dll",SetLastError=true)] public static extern uint SendInput(uint count,Input[] input,int size);
    }
}
