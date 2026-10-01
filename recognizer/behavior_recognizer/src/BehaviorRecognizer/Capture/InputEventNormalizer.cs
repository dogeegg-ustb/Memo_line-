using System.Numerics;
using BehaviorRecognizer.Abstractions.Config;
using BehaviorRecognizer.Abstractions.Input;

namespace BehaviorRecognizer.Capture;

public sealed class InputEventNormalizer : IInputEventNormalizer
{
    private readonly object _sync = new();
    private ContactState _lastContact = ContactState.OutOfRange;
    private bool[]? _lastButtons;
    private PenProfile _profile;

    public InputEventNormalizer(PenProfile profile)
    {
        _profile = profile;
    }

    public void UpdateProfile(PenProfile profile) => _profile = profile;

    public IEnumerable<InputEvent> Normalize(RawInputReport report, string sessionId, ulong sequence)
    {
        List<InputEvent> events = [];

        if (report.IsOutOfRange)
        {
            lock (_sync)
            {
                if (_lastContact != ContactState.OutOfRange)
                {
                    if (_lastContact == ContactState.Contact)
                    {
                        events.Add(Create(report, sessionId, sequence, InputEventType.PenUp, ContactState.OutOfRange, pressure: 0f));
                    }

                    _lastContact = ContactState.OutOfRange;
                }
            }

            events.Add(Create(report, sessionId, sequence, InputEventType.PenHover, ContactState.OutOfRange));
            return events;
        }

        var normalizedPressure = NormalizePressure(report.Pressure, report.MaxPressure);
        var contact = ResolveContact(normalizedPressure, report.IsNearProximity);
        var rawPressure = report.Pressure;

        lock (_sync)
        {
            if (report.PenButtons is not null && !ButtonsEqual(report.PenButtons, _lastButtons))
            {
                events.Add(Create(report, sessionId, sequence, InputEventType.PenButtonChanged, contact, rawPressure));
                _lastButtons = (bool[])report.PenButtons.Clone();
            }

            switch (contact)
            {
                case ContactState.Contact when _lastContact != ContactState.Contact:
                    events.Add(Create(report, sessionId, sequence, InputEventType.PenDown, contact, rawPressure));
                    break;
                case ContactState.Contact:
                    events.Add(Create(report, sessionId, sequence, InputEventType.PenMove, contact, rawPressure));
                    break;
                case ContactState.Hover when _lastContact == ContactState.Contact:
                    events.Add(Create(report, sessionId, sequence, InputEventType.PenUp, contact, rawPressure));
                    if (_profile.HoverTracking)
                        events.Add(Create(report, sessionId, sequence, InputEventType.PenHover, contact, rawPressure));
                    break;
                case ContactState.Hover:
                    if (_profile.HoverTracking)
                        events.Add(Create(report, sessionId, sequence, InputEventType.PenHover, contact, rawPressure));
                    break;
            }

            _lastContact = contact;
        }

        return events;
    }

    private ContactState ResolveContact(float? normalizedPressure, bool nearProximity)
    {
        if (normalizedPressure is > 0f && normalizedPressure.Value >= _profile.TipThreshold)
            return ContactState.Contact;

        if (nearProximity || normalizedPressure is > 0f)
            return ContactState.Hover;

        return ContactState.OutOfRange;
    }

    private float? NormalizePressure(float? raw, float? maxPressure)
    {
        if (raw is null)
            return null;

        var max = maxPressure is > 0 ? maxPressure.Value : 1f;
        var linear = Math.Clamp(raw.Value / max, 0f, 1f) * _profile.PressureSensitivity;
        linear = Math.Clamp(linear, 0f, 1f);
        return ApplyCurve(linear, _profile.PressureCurve);
    }

    private static float ApplyCurve(float value, IReadOnlyList<float> curve)
    {
        if (curve.Count < 2)
            return value;

        var scaled = value * (curve.Count - 1);
        var index = (int)Math.Floor(scaled);
        if (index >= curve.Count - 1)
            return curve[^1];

        var t = scaled - index;
        return curve[index] + (curve[index + 1] - curve[index]) * t;
    }

    private static bool ButtonsEqual(bool[]? a, bool[]? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null || a.Length != b.Length)
            return false;
        return a.AsSpan().SequenceEqual(b);
    }

    private static InputEvent Create(
        RawInputReport report,
        string sessionId,
        ulong sequence,
        InputEventType type,
        ContactState contact,
        float? pressure = null)
    {
        Vector2? position = report.X is null || report.Y is null
            ? null
            : new Vector2(report.X.Value, report.Y.Value);

        Vector2? tilt = report.TiltX is null || report.TiltY is null
            ? null
            : new Vector2(report.TiltX.Value, report.TiltY.Value);

        return new InputEvent
        {
            Type = type,
            Timestamp = report.Timestamp,
            SessionId = sessionId,
            DeviceId = report.DeviceId,
            Sequence = sequence,
            Position = position,
            Pressure = pressure ?? (report.Pressure is null || report.MaxPressure is null or 0
                ? null
                : report.Pressure / report.MaxPressure),
            Tilt = tilt,
            ContactState = contact,
            PenButtons = report.PenButtons is null ? null : (bool[])report.PenButtons.Clone()
        };
    }
}
