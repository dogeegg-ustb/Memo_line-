namespace BehaviorRecognizer.Capture;

/// <summary>Routes low-level mouse notifications independently of the hook's message pump.</summary>
internal sealed class WindowsMouseInputRouter(UnifiedInputCapture capture, bool passivePen, LayerSaveGuard? guard = null)
{
    private int _buttonsDown;
    private bool _passivePenDown;

    internal bool Observe(int message, int x, int y, uint mouseData, uint flags, nuint extraInfo)
    {
        // Recorder replay/save input was observed before buffering. Do not
        // activate it twice or send it back through the save guard.
        if (extraInfo == WindowsInputHooks.RecorderInputTag) return false;
        bool penOrTouch = (extraInfo & (nuint)0xFFFFFF00) == (nuint)0xFF515700;
        bool touch = (extraInfo & (nuint)0x80) != 0;
        if (penOrTouch)
        {
            if (touch) return false;
            if (passivePen)
            {
                if (message == 0x0201) _passivePenDown = true;
                if (message != 0x0200 || _passivePenDown)
                    capture.PostMouse(message, x, y, (int)mouseData, passivePen: true);
                if (message == 0x0202) _passivePenDown = false;
            }
            return guard?.Mouse(message, x, y, mouseData, penCompatibility: true) == true;
        }

        // LLMHF_INJECTED and LLMHF_LOWER_IL_INJECTED are accepted just like
        // physical mouse input, including clicks, drags and both wheel axes.
        int button = message switch
        {
            0x0201 or 0x0202 => 1, 0x0204 or 0x0205 => 2,
            0x0207 or 0x0208 => 4,
            0x020B or 0x020C => ((mouseData >> 16) & 0xffff) == 1 ? 8 : 16,
            _ => 0
        };
        if (message is 0x0201 or 0x0204 or 0x0207 or 0x020B) _buttonsDown |= button;
        if (message != 0x0200 || _buttonsDown != 0)
            capture.PostMouse(message, x, y, (int)mouseData);
        if (message is 0x0202 or 0x0205 or 0x0208 or 0x020C) _buttonsDown &= ~button;
        return guard?.Mouse(message, x, y, mouseData) == true;
    }
}
