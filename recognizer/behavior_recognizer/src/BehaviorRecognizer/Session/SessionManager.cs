using BehaviorRecognizer.Abstractions.Session;

namespace BehaviorRecognizer.Session;

public sealed class SessionManager : ISessionManager
{
    private readonly object _sync = new();
    private SessionInfo? _current;

    public SessionInfo? Current
    {
        get
        {
            lock (_sync)
                return _current;
        }
    }

    public event EventHandler<SessionState>? StateChanged;

    public Task<SessionInfo> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var root = ApplicationPaths.EnsureLayout();
        var info = new SessionInfo
        {
            SessionId = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            State = SessionState.Created,
            DataDirectory = root.Sessions
        };

        lock (_sync)
            _current = info;

        StateChanged?.Invoke(this, info.State);
        return Task.FromResult(info);
    }

    public Task TransitionAsync(SessionState next, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_current is null)
                throw new InvalidOperationException("No active session.");

            if (!IsAllowed(_current.State, next))
                throw new InvalidOperationException($"Illegal session transition {_current.State} -> {next}");

            _current.State = next;
            StateChanged?.Invoke(this, next);
        }

        return Task.CompletedTask;
    }

    private static bool IsAllowed(SessionState from, SessionState to) => (from, to) switch
    {
        (SessionState.Created, SessionState.Initializing) => true,
        (SessionState.Initializing, SessionState.Ready) => true,
        (SessionState.Initializing, SessionState.Recovering) => true,
        (SessionState.Recovering, SessionState.Ready) => true,
        (SessionState.Ready, SessionState.Recording) => true,
        (SessionState.Recording, SessionState.Paused) => true,
        (SessionState.Paused, SessionState.Recording) => true,
        (SessionState.Recording, SessionState.Stopped) => true,
        (SessionState.Paused, SessionState.Stopped) => true,
        (SessionState.Ready, SessionState.Stopped) => true,
        (SessionState.Initializing, SessionState.Stopped) => true,
        (_, SessionState.Stopped) => true,
        _ => false
    };
}

public sealed class ApplicationPaths
{
    public required string Root { get; init; }
    public required string Config { get; init; }
    public required string Cache { get; init; }
    public required string Sessions { get; init; }
    /// <summary>笔迹输出根目录（其下为 stroke/ 子目录）。</summary>
    public required string StrokeRoot { get; init; }
    public required string Exports { get; init; }
    public required string Logs { get; init; }
    public required string Drivers { get; init; }
    public required string Bootstrap { get; init; }

    public static ApplicationPaths EnsureLayout(string? root = null)
    {
        // 默认保存到 <exe-dir>/procedure（首次运行自动创建）。
        var baseRoot = root ?? Path.Combine(AppContext.BaseDirectory, "procedure");

        var paths = new ApplicationPaths
        {
            Root = baseRoot,
            Config = Path.Combine(baseRoot, "config"),
            Cache = Path.Combine(baseRoot, "cache"),
            Sessions = Path.Combine(baseRoot, "sessions"),
            StrokeRoot = baseRoot, // OutputRoot；最终写入 <Root>/stroke/
            Exports = Path.Combine(baseRoot, "exports"),
            Logs = Path.Combine(baseRoot, "logs"),
            Drivers = Path.Combine(baseRoot, "drivers"),
            Bootstrap = Path.Combine(baseRoot, "bootstrap")
        };

        foreach (var dir in new[]
                 {
                     paths.Root, paths.Config, paths.Cache, paths.Sessions,
                     Path.Combine(paths.StrokeRoot, "stroke"), // 笔迹目录
                     paths.Exports, paths.Logs, paths.Drivers, paths.Bootstrap
                 })
        {
            Directory.CreateDirectory(dir);
        }

        // Seed default config into user config dir if missing.
        var bundled = Path.Combine(AppContext.BaseDirectory, "config", "default_pen_profile.json");
        var target = Path.Combine(paths.Config, "default_pen_profile.json");
        if (File.Exists(bundled) && !File.Exists(target))
            File.Copy(bundled, target);

        return paths;
    }
}
