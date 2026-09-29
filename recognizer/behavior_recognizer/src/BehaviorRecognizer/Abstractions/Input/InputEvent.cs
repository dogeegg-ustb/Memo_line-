using System.Numerics;
using System.Text.Json.Serialization;

namespace BehaviorRecognizer.Abstractions.Input;

/// <summary>
/// Unified bus event. Downstream recorders must consume this type only,
/// never raw driver objects.
/// </summary>
public sealed class InputEvent
{
    public required InputEventType Type { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string SessionId { get; init; }

    public required string DeviceId { get; init; }

    public required ulong Sequence { get; init; }

    public Vector2? Position { get; init; }

    /// <summary>Raw device pressure value.</summary>
    public float? Pressure { get; init; }

    public Vector2? Tilt { get; init; }

    public ContactState ContactState { get; init; }

    public bool[]? PenButtons { get; init; }

    public string? Message { get; init; }

    /// <summary>Extensible bag for context / future recorder fields.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? Extensions { get; init; }

    public InputEvent CloneWithType(InputEventType type) => new()
    {
        Type = type,
        Timestamp = Timestamp,
        SessionId = SessionId,
        DeviceId = DeviceId,
        Sequence = Sequence,
        Position = Position,
        Pressure = Pressure,
        Tilt = Tilt,
        ContactState = ContactState,
        PenButtons = PenButtons is null ? null : (bool[])PenButtons.Clone(),
        Message = Message,
        Extensions = Extensions is null ? null : new Dictionary<string, object?>(Extensions)
    };
}
