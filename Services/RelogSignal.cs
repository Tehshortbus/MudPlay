namespace FujinTerm.Services;

// One-shot flag coordinating a deliberate "relog" — a graceful exit plus a
// forced reconnect-and-login — across the engine that requests it and the
// connection lifecycle that performs it. Set by Game.Remote.RelogHandler right
// before it sends the configured exit command on the wire; consumed by
// MainWindowViewModel's Disconnected handler to force an unconditional
// dial-back regardless of the per-BBS reconnect toggles.
//
// Deliberately the inverse of HangupSignal. A hangup suppresses both the
// reactive auto-reconnect AND the next entry automation, leaving the user at
// the screen to decide. A relog wants the opposite: it forces the reconnect and
// lets the normal login automation run, so the round-trip is fully automatic
// and the character ends up back in-game without manual input. Because relog
// uses this separate signal (never HangupSignal), the entry latch is not
// suppressed — login proceeds as on any fresh connect.
//
// In-memory only — wipes on app close. A single flag (not two like
// HangupSignal) because relog has exactly one consumer: the Disconnected
// handler that decides to force the dial-back.
public sealed class RelogSignal
{
    private bool _relogPending;

    // Arm the one-shot relog flag. Called by Game.Remote.RelogHandler just
    // before the wire exit command lands, so the Disconnected handler (which
    // races with the wire round-trip) sees the intent.
    public void SignalRelog() => _relogPending = true;

    // Read + clear the relog-intent flag. Returns true exactly once after each
    // SignalRelog call. Consumed by MainWindowViewModel's Disconnected handler
    // to classify the drop as a relog and force the unconditional reconnect.
    public bool ConsumeRelogIntent()
    {
        bool was = _relogPending;
        _relogPending = false;
        return was;
    }

    // Test seam — non-mutating read of the flag. Lets unit tests assert "relog
    // is armed" without consuming it. Production callers always go through
    // ConsumeRelogIntent.
    internal bool PeekForTests() => _relogPending;
}
