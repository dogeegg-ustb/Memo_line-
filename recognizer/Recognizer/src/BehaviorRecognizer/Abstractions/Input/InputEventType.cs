namespace BehaviorRecognizer.Abstractions.Input;

public enum InputEventType
{
    PenDown,
    PenMove,
    PenUp,
    PenHover,
    PenButtonChanged,
    TabletDetected,
    DeviceChanged,
    ConfigurationApplied,
    EnvironmentCapabilityChanged,
    ContextChanged,
    Custom
}

public enum ContactState
{
    OutOfRange,
    Hover,
    Contact
}
