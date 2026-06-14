namespace FujinTerm.Services;

/// <summary>
/// One-shot flag coordinating a deliberate "relog" — a graceful exit
/// plus a forced reconnect-and-login — across the engine that requests
/// it and the connection lifecycle that performs it. Set by
/// <see cref="Game.Remote.RelogHandler"/> right before it sends the
/// configured exit command on the wire; consumed by
/// <see cref="ViewModels.MainWindowViewModel"/>'s Disconnected handler
/// to force an unconditional dial-back regardless of the per-BBS
/// reconnect toggles.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the inverse of <see cref="HangupSignal"/>. A hangup
/// suppresses both the reactive auto-reconnect AND the next entry
/// automation, leaving the user at the screen to decide. A relog wants
/// the opposite: it forces the reconnect and lets the normal login
/// automation run, so the round-trip is fully automatic and the
/// character ends up back in-game without manual input. Because relog
/// uses this separate signal (never <see cref="HangupSignal"/>), the
/// entry latch is not suppressed — login proceeds as on any fresh
/// connect.
/// </para>
/// <para>
/// In-memory only — wipes on app close. A single flag (not two like
/// <see cref="HangupSignal"/>) because relog has exactly one consumer:
/// the Disconnected handler that decides to force the dial-back.
/// </para>
/// </remarks>
public sealed class RelogSignal
{
    private bool _relogPending;

    /// <summary>
    /// Arm the one-shot relog flag. Called by
    /// <see cref="Game.Remote.RelogHandler"/> just before the wire exit
    /// command lands, so the Disconnected handler (which races with the
    /// wire round-trip) sees the intent.
    /// </summary>
    public void SignalRelog() => _relogPending = true;

    /// <summary>
    /// Read + clear the relog-intent flag. Returns <c>true</c> exactly
    /// once after each <see cref="SignalRelog"/> call. Consumed by
    /// <see cref="ViewModels.MainWindowViewModel"/>'s Disconnected
    /// handler to classify the drop as a relog and force the
    /// unconditional reconnect.
    /// </summary>
    public bool ConsumeRelogIntent()
    {
        bool was = _relogPending;
        _relogPending = false;
        return was;
    }

    /// <summary>
    /// Test seam — non-mutating read of the flag. Lets unit tests assert
    /// "relog is armed" without consuming it. Production callers always
    /// go through <see cref="ConsumeRelogIntent"/>.
    /// </summary>
    internal bool PeekForTests() => _relogPending;
}
