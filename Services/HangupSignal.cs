namespace FujinTerm.Services;

/// <summary>
/// Two one-shot flags coordinating "we deliberately hung up" intent
/// across the engines that need to react to it. Set by every path that
/// intentionally drops the carrier (remote <c>@hangup</c>, future
/// hang-up-if-naked / hang-up-if-low-HP automation); consumed by
/// <see cref="ViewModels.MainWindowViewModel"/> (to suppress the
/// reactive auto-reconnect) and by
/// <see cref="Game.MainMenuEntryAutomation"/> (to suppress the
/// auto-entry latch on the next connect, so the user reads what's on
/// screen and types <c>E</c> themselves).
/// </summary>
/// <remarks>
/// <para>
/// Why two flags instead of one: the consumers fire at different
/// moments. <see cref="ConsumeDisconnectIntent"/> fires inside the
/// Disconnected event right after the server drops us; the entry-
/// suppression flag survives that disconnect through the user's
/// manual reconnect and the login automation walk, only consumed by
/// <see cref="ConsumeSuppressEntry"/> when the main-menu pattern
/// match is about to arm the entry latch. Both clear themselves
/// on consume so behavior reverts to normal automatically.
/// </para>
/// <para>
/// In-memory only — wipes on app close. A user who closes the app
/// after a hangup and reopens it gets a fresh slate, which is the
/// intent: they made a deliberate choice to close, so the
/// "I'm-in-a-dangerous-spot" context doesn't survive the restart.
/// </para>
/// </remarks>
public sealed class HangupSignal
{
    private bool _disconnectExpected;
    private bool _suppressNextEntry;

    /// <summary>
    /// Arm both one-shot flags. Called by every engine that's about
    /// to send the configured exit command on the wire as a
    /// deliberate hang-up: <see cref="Game.Remote.HangupHandler"/>
    /// (today), plus the future hang-up-if-naked /
    /// hang-up-if-low-HP automation engines (Phase 13).
    /// </summary>
    public void SignalHangup()
    {
        _disconnectExpected = true;
        _suppressNextEntry = true;
    }

    /// <summary>
    /// Read + clear the disconnect-intent flag. Returns <c>true</c>
    /// exactly once after each <see cref="SignalHangup"/> call.
    /// Consumed by <see cref="ViewModels.MainWindowViewModel"/>'s
    /// Disconnected handler so the drop is classified as
    /// <c>HangupInitiated</c> rather than carrier-lost / no-response,
    /// suppressing the reactive auto-reconnect path.
    /// </summary>
    public bool ConsumeDisconnectIntent()
    {
        bool was = _disconnectExpected;
        _disconnectExpected = false;
        return was;
    }

    /// <summary>
    /// Read + clear the suppress-entry flag. Returns <c>true</c>
    /// exactly once after each <see cref="SignalHangup"/> call.
    /// Consumed by <see cref="Game.MainMenuEntryAutomation.Arm"/> so
    /// the entry latch stays closed on the first connect after a
    /// hangup — user manually types their entry command (and the
    /// post-entry <c>stat</c>/<c>exp</c>/<c>i</c> refresh stays off
    /// the wire) so they can read what's on the screen and react.
    /// </summary>
    public bool ConsumeSuppressEntry()
    {
        bool was = _suppressNextEntry;
        _suppressNextEntry = false;
        return was;
    }

    /// <summary>
    /// Test seam — non-mutating read of both flags. Lets unit tests
    /// assert "flag is currently set" without consuming it. Production
    /// callers always go through the Consume methods.
    /// </summary>
    internal (bool DisconnectExpected, bool SuppressNextEntry) PeekForTests()
        => (_disconnectExpected, _suppressNextEntry);
}
