namespace FujinTerm.Services;

// Two one-shot flags coordinating "we deliberately hung up" intent across the
// engines that need to react to it. Set by every path that intentionally drops
// the carrier (remote @hangup, future hang-up-if-naked / hang-up-if-low-HP
// automation); consumed by MainWindowViewModel (to suppress the reactive
// auto-reconnect) and by Game.MainMenuEntryAutomation (to suppress the
// auto-entry latch on the next connect, so the user reads what's on screen and
// types E themselves).
//
// Why two flags instead of one: the consumers fire at different moments.
// ConsumeDisconnectIntent fires inside the Disconnected event right after the
// server drops us; the entry-suppression flag survives that disconnect through
// the user's manual reconnect and the login automation walk, only consumed by
// ConsumeSuppressEntry when the main-menu pattern match is about to arm the
// entry latch. Both clear themselves on consume so behavior reverts to normal
// automatically.
//
// In-memory only — wipes on app close. A user who closes the app after a hangup
// and reopens it gets a fresh slate, which is the intent: they made a deliberate
// choice to close, so the "I'm-in-a-dangerous-spot" context doesn't survive the
// restart.
public sealed class HangupSignal
{
    private bool _disconnectExpected;
    private bool _suppressNextEntry;

    // Arm both one-shot flags. Called by every engine that's about to send the
    // configured exit command on the wire as a deliberate hang-up:
    // Game.Remote.HangupHandler (today), plus the future hang-up-if-naked /
    // hang-up-if-low-HP automation engines.
    public void SignalHangup()
    {
        _disconnectExpected = true;
        _suppressNextEntry = true;
    }

    // Read + clear the disconnect-intent flag. Returns true exactly once after
    // each SignalHangup call. Consumed by MainWindowViewModel's Disconnected
    // handler so the drop is classified as HangupInitiated rather than
    // carrier-lost / no-response, suppressing the reactive auto-reconnect path.
    public bool ConsumeDisconnectIntent()
    {
        bool was = _disconnectExpected;
        _disconnectExpected = false;
        return was;
    }

    // Read + clear the suppress-entry flag. Returns true exactly once after each
    // SignalHangup call. Consumed by Game.MainMenuEntryAutomation.Arm so the
    // entry latch stays closed on the first connect after a hangup — user
    // manually types their entry command (and the post-entry stat/exp/i refresh
    // stays off the wire) so they can read what's on the screen and react.
    public bool ConsumeSuppressEntry()
    {
        bool was = _suppressNextEntry;
        _suppressNextEntry = false;
        return was;
    }

    // Test seam — non-mutating read of both flags. Lets unit tests assert "flag
    // is currently set" without consuming it. Production callers always go
    // through the Consume methods.
    internal (bool DisconnectExpected, bool SuppressNextEntry) PeekForTests()
        => (_disconnectExpected, _suppressNextEntry);
}
