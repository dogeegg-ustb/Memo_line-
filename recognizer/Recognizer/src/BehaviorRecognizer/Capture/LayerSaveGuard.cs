using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;

namespace BehaviorRecognizer.Capture;

/// <summary>Hook callbacks only buffer. A dedicated worker asks the supplied AHK script
/// to save, then replays the original input in order without recapturing injected events.</summary>
public sealed class LayerSaveGuard : IAsyncDisposable
{
    private sealed record Configuration(bool Enabled, int[] Region, HashSet<(int,int)> Keys,
        string Executable, long Updated);
    private readonly MemolineWriter _writer;
    private readonly Action<object> _notify;
    private readonly Action<Process>? _registerProcess;
    private readonly object _sync = new();
    private sealed record Buffered(Input Input, bool SaveFirst, long Ticks, int Modifiers);
    private readonly HashSet<int> _blockedKeys = [];
    private uint _blockedMouseButtons;
    private volatile bool _workerReady;
    private readonly List<Buffered> _pending = [];
    private Configuration _config = new(false, [], [], "", 0);
    private bool _busy, _closed;
    private Task _worker = Task.CompletedTask;
    private Process? _ahk;
    private readonly SemaphoreSlim _ahkLock = new(1,1);
    private Task _warmup = Task.CompletedTask;
    private bool _warmupStarted;
    private string? _reportedError;

    public LayerSaveGuard(MemolineWriter writer, Action<object> notify, Action<Process>? registerProcess=null)
        => (_writer,_notify,_registerProcess) = (writer,notify,registerProcess);

    public void Configure(JsonElement data)
    {
        string exe = data.GetProperty("autoHotkeyPath").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(exe))
            exe = new[] { Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),"Programs","AutoHotkey","v2","AutoHotkey64.exe"),
                @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe" }.FirstOrDefault(File.Exists) ?? "";
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

    private bool Available(Configuration config) => !_closed && config.Enabled && config.Region.Length == 4 &&
        _workerReady && _ahk is { HasExited:false } && Stopwatch.GetTimestamp()-config.Updated < Stopwatch.Frequency*2 && CspWindowProbe.IsCspForeground();

    public bool Keyboard(int vk, uint scan, bool extended, bool down)
    {
        // Modifiers and ordinary shortcuts are observed and always forwarded.
        if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) return false;
        var config = Volatile.Read(ref _config);
        lock (_sync)
            if (!_blockedKeys.Contains(vk) && !config.Keys.Any(binding=>binding.Item1==vk)) return false;
        int mods = (Held(17)?4:0)|(Held(16)?2:0)|(Held(18)?1:0);
        bool trigger = down && !Held(91) && !Held(92) && config.Keys.Contains((vk,mods)) && Available(config);
        lock (_sync)
        {
            if (!trigger && !_blockedKeys.Contains(vk)) return false;
            if (trigger) _blockedKeys.Add(vk);
            var input = new Input { Type=1, Union=new() { Keyboard=new() { Vk=(ushort)vk, Scan=(ushort)scan,
                Flags=(extended?1u:0u)|(down?0u:2u), Extra=WindowsInputHooks.RecorderInputTag } } };
            return Buffer(input,trigger,config,mods);
        }
    }

    private static uint MouseButton(int message,uint data) => message switch {
        0x201 or 0x202=>1, 0x204 or 0x205=>2, 0x207 or 0x208=>4,
        0x20B or 0x20C=>(data>>16)==1?8u:16u, _=>0 };

    public bool Mouse(int message, int x, int y, uint data)
    {
        bool click = message is 0x201 or 0x204 or 0x207 or 0x20B;
        var config = Volatile.Read(ref _config);
        bool trigger = click && Available(config) && x>=config.Region[0] && y>=config.Region[1] &&
            x<config.Region[0]+config.Region[2] && y<config.Region[1]+config.Region[3] && CspWindowProbe.IsCspPoint(x,y);
        uint button=MouseButton(message,data);
        lock (_sync)
        {
            // Only a guarded button's continuation is held. Unrelated pointer input passes through.
            if (!trigger && !(message==0x200 && _blockedMouseButtons!=0) && (_blockedMouseButtons&button)==0) return false;
            uint flags = message switch { 0x200=>1,0x201=>2,0x202=>4,0x204=>8,0x205=>16,
                0x207=>32,0x208=>64,0x20B=>128,0x20C=>256,_=>0 };
            if (flags==0) return false;
            if (trigger) _blockedMouseButtons|=button;
            int left=Native.GetSystemMetrics(76), top=Native.GetSystemMetrics(77);
            int width=Native.GetSystemMetrics(78), height=Native.GetSystemMetrics(79);
            var input = new Input { Type=0, Union=new() { Mouse=new() {
                X=(int)((long)(x-left)*65535/Math.Max(1,width-1)), Y=(int)((long)(y-top)*65535/Math.Max(1,height-1)),
                Data=message is 0x20B or 0x20C ? data>>16 : 0,
                Flags=flags|1|0x8000|0x4000, Extra=WindowsInputHooks.RecorderInputTag } } };
            return Buffer(input,trigger,config,0);
        }
    }

    private bool Buffer(Input input, bool trigger, Configuration config, int modifiers)
    {
        lock (_sync)
        {
            if (_closed || (!_busy && !trigger)) return false;
            _pending.Add(new(input,trigger,_writer.NowTicks,modifiers));
            if (!_busy)
            {
                _busy=true;
                var hwnd = Native.GetForegroundWindow();
                _worker = Task.Run(()=>SaveAndReleaseAsync(config,hwnd));
            }
            return true;
        }
    }

    private static Input KeyInput(ushort vk,bool down) => new() { Type=1, Union=new() { Keyboard=new() {
        Vk=vk, Flags=(down?0u:2u)|(vk is 0xA3 or 0xA5 or 0x5B or 0x5C?1u:0u), Extra=WindowsInputHooks.RecorderInputTag } } };

    private static IEnumerable<Input> ReplayInputs(Buffered item)
    {
        if (!item.SaveFirst || item.Input.Type!=1) { yield return item.Input; yield break; }
        // Modifier key-ups are never swallowed. Restore the original shortcut's chord
        // locally during replay, then return to the user's current modifier state.
        ushort[] modifiers=[0xA2,0xA3,0xA0,0xA1,0xA4,0xA5,0x5B,0x5C];
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
                _writer.AppendState("clipSaveRequest",ticks,[],new { saveId, reason="beforeLayerOperation", confirmation="requestOnly",
                    releasePolicy="afterSaveInputDispatch", interceptedInput=new { type=batch[0].Input.Type,
                        vk=batch[0].Input.Type==1?(int?)batch[0].Input.Union.Keyboard.Vk:null,modifiers=batch[0].Modifiers } },"immediate");
                try
                {
                    if (!_workerReady || _ahk is not { HasExited:false })
                        throw new IOException("Save worker unavailable; original layer input will be released.");
                    await _ahk.StandardInput.WriteLineAsync($"{hwnd.ToInt64()}");
                    await _ahk.StandardInput.FlushAsync();
                    string? response=await _ahk.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(250));
                    if (response?.TrimStart('﻿') != "save-input-sent") throw new IOException(response ?? "AHK worker exited.");
                    dispatchedTicks=_writer.NowTicks;
                }
                catch(Exception ex)
                {
                    _workerReady=false;
                    error=ex.Message;
                    try { if (_ahk is { HasExited:false }) _ahk.Kill(); }
                    catch (Exception cleanupError) { error += "; cleanup: "+cleanupError.Message; }
                }
            }
            bool released=false;
            // Never hold _sync across SendInput: the hook thread may need it to
            // handle a real input while Windows is dispatching our replay.
            if (Native.GetForegroundWindow()==hwnd)
            {
                var items=batch.SelectMany(ReplayInputs).ToArray();
                released=Native.SendInput((uint)items.Length,items,Marshal.SizeOf<Input>())==items.Length;
                if (!released) error=(error is null?"":error+"; ")+"SendInput did not replay every buffered input.";
            }
            else error=(error is null?"":error+"; ")+"CSP lost foreground; guarded input cancelled.";
            lock (_sync)
            {
                _blockedKeys.RemoveWhere(v=>!_pending.Any(i=>i.Input.Type==1&&i.Input.Union.Keyboard.Vk==v));
                if (!_pending.Any(i=>i.Input.Type==0)) _blockedMouseButtons=0;
            }
            if (save)
            {
                long releasedTicks=_writer.NowTicks;
                Console.WriteLine($"[图层保存保护] vk={batch[0].Input.Union.Keyboard.Vk} 保存已派发={dispatchedTicks is not null} 原指令已释放={released} {error}");
                _writer.AppendState("saveGuardResult",ticks,[],new { saveId,released,releasedTicks,error,
                    saveInputDispatchedTicks=dispatchedTicks, originalInputPolicy="releaseOnSaveFailureWhileCspForeground",
                    interceptionDurationMs=(releasedTicks-ticks)*1000.0/Stopwatch.Frequency,
                    cspAcceptanceConfirmed=false, saveCompletionConfirmed=false },"immediate");
                _notify(new { type="guardReleased",ticks=releasedTicks,saveTicks=ticks,saveId,released,error });
            }
        }
    }

    private async Task EnsureSaveWorkerAsync(Configuration config)
    {
        await _ahkLock.WaitAsync();
        string stage="processLaunch";
        try
        {
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

    private static bool Held(int vk) => (Native.GetAsyncKeyState(vk)&0x8000)!=0;
    public async ValueTask DisposeAsync()
    {
        _closed=true;
        await _warmup;
        await _worker;
        if (_ahk is { HasExited:false }) { _ahk.StandardInput.Close(); await _ahk.WaitForExitAsync(); }
        _ahk?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
        [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X,Y; public uint Data,Flags,Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Vk,Scan; public uint Flags,Time; public nuint Extra; }
    private static class Native {
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int metric);
        [DllImport("user32.dll",SetLastError=true)] public static extern uint SendInput(uint count,Input[] input,int size);
    }
}
