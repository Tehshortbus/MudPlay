namespace FujinTerm.Services;

// One-flag pause switch for every engine-driven wire send. Raised while the user
// is in a flow that mustn't be polluted by automatic commands (today:
// Game.SuicidePasswordTracker's password-entry prompts — a stray par poll
// mid-flow becomes the password). MainWindowViewModel wraps every engine's
// SetWireSender callback through WrapEngineSender so flipping IsLocked silently
// no-ops every engine until cleared.
//
// User-typed input does NOT go through the wrapped path — it flows from
// TerminalControl → LocalInputBuffer → directly into
// MainWindowViewModel.SendUserInput. So even with the gate locked, the user can
// still type their password normally; only background engines are gated.
public sealed class EngineSendGate
{
    // True while engine wire-sends must drop on the floor. Set to true when
    // entering a sensitive prompt; set back to false on the corresponding
    // terminator. Defaults to false so engines start in their normal active
    // state.
    public bool IsLocked { get; set; }

    // Wrap an engine's raw Action<byte[]> wire-sender so it short-circuits while
    // IsLocked is true.
    public Action<byte[]> WrapEngineSender(Action<byte[]> rawSender)
    {
        ArgumentNullException.ThrowIfNull(rawSender);
        return bytes =>
        {
            if (IsLocked) return;
            rawSender(bytes);
        };
    }
}
