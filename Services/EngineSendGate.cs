namespace FujinTerm.Services;

/// <summary>
/// One-flag pause switch for every engine-driven wire send. Raised
/// while the user is in a flow that mustn't be polluted by automatic
/// commands (today: <see cref="Game.SuicidePasswordTracker"/>'s
/// password-entry prompts — a stray <c>par</c> poll mid-flow becomes
/// the password). MainWindowViewModel wraps every engine's
/// <c>SetWireSender</c> callback through
/// <see cref="WrapEngineSender"/> so flipping <see cref="IsLocked"/>
/// silently no-ops every engine until cleared.
/// </summary>
/// <remarks>
/// User-typed input does NOT go through the wrapped path — it flows
/// from <c>TerminalControl</c> → <c>LocalInputBuffer</c> → directly
/// into <c>MainWindowViewModel.SendUserInput</c>. So even with the
/// gate locked, the user can still type their password normally;
/// only background engines are gated.
/// </remarks>
public sealed class EngineSendGate
{
    /// <summary>
    /// True while engine wire-sends must drop on the floor. Set to
    /// <c>true</c> when entering a sensitive prompt; set back to
    /// <c>false</c> on the corresponding terminator. Defaults to
    /// <c>false</c> so engines start in their normal active state.
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// Wrap an engine's raw <c>Action&lt;byte[]&gt;</c> wire-sender so
    /// it short-circuits while <see cref="IsLocked"/> is true.
    /// </summary>
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
