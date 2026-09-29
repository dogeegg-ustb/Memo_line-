namespace BehaviorRecognizer.Abstractions.Session;

public enum SessionState
{
    Created,
    Initializing,
    Ready,
    Recording,
    Paused,
    Recovering,
    Stopped
}

public sealed class SessionInfo
{
    public required string SessionId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public SessionState State { get; set; } = SessionState.Created;
    public string? DataDirectory { get; init; }
}

public interface ISessionManager
{
    SessionInfo? Current { get; }

    event EventHandler<SessionState>? StateChanged;

    Task<SessionInfo> CreateAsync(CancellationToken cancellationToken = default);

    Task TransitionAsync(SessionState next, CancellationToken cancellationToken = default);
}
