namespace FujinTerm.Services;

// Pause switch for every engine-driven wire send. Held while the app is in a flow
// that mustn't be polluted by automatic commands — and more than one such flow can
// overlap, so it tracks a SET of named holds rather than a single flag and stays
// locked until every hold is released. Current holders:
//   - Game.SuicidePasswordTracker, around each password-entry prompt (a stray par
//     poll mid-flow would become the password).
//   - Game.PlayerDroppedGate, while the local character is mortally wounded (HP <=
//     0): the game rejects every action command with "You may not do that while you
//     are mortally wounded!", so engine sends are pure noise until we recover.
// MainWindowViewModel wraps every engine's SetWireSender callback through
// WrapEngineSender, so a raised hold silently no-ops every engine until cleared.
//
// User-typed input does NOT go through the wrapped path — it flows from
// TerminalControl -> LocalInputBuffer -> directly into
// MainWindowViewModel.SendUserInput. So even while held, the user can still type
// (their password, a manual command) normally; only background engines are gated.
//
// The emergency low-HP hangup is the one automatic send that must survive a hold —
// hanging up is still allowed at 0 HP or below. HealthManager sends it through a
// separate, un-wrapped sender (SetHangupWireSender), so it isn't gated here.
//
// Single-threaded: every holder flips these on the UI thread (router handlers and
// PlayerState.PropertyChanged both marshal upstream), and the wrapper reads on the
// same thread, so the plain HashSet needs no lock.
public sealed class EngineSendGate
{
    private readonly HashSet<string> _holds = new(StringComparer.Ordinal);

    // True while any hold is active — engine wire-sends drop on the floor.
    public bool IsLocked => _holds.Count > 0;

    // Raise a named hold. Idempotent per reason.
    public void Hold(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        _holds.Add(reason);
    }

    // Release a named hold. Sends resume once the last hold clears.
    public void Release(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        _holds.Remove(reason);
    }

    // Wrap an engine's raw Action<byte[]> wire-sender so it short-circuits while any
    // hold is active.
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
