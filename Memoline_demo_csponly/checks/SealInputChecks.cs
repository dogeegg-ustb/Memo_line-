using System.Text.Json;

namespace MemolineDemo;

internal static class SealInputChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        JsonElement Key(string key, int vk, params string[] modifiers)
            => JsonSerializer.SerializeToElement(new { key, vk, modifiers });

        // Recorded after a recording end: the user pressed Win+Shift+S for a screenshot.
        Check(AggregateSession.SystemChord(Key("Win", 91, "Win")), "the Windows key itself never reaches CSP");
        Check(AggregateSession.SystemChord(Key("Shift", 161, "Win", "Shift")), "Shift held with Win is part of a system chord");
        Check(AggregateSession.SystemChord(Key("S", 83, "Win", "Shift")), "Win+Shift+S is a system screenshot");
        Check(!AggregateSession.SystemChord(Key("S", 83, "Ctrl")), "Ctrl+S still reaches CSP");
        Check(!AggregateSession.SystemChord(Key("Z", 90)), "a plain key still reaches CSP");
        return checks;
    }
}
