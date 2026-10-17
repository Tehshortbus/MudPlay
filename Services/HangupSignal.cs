namespace MudPlay.Services;

// Two one-shot flags coordinating "we deliberately hung up" intent across the
// engines that need to react to it. Set by every path that intentionally drops
// the carrier (remote @hangup, HealthManager's low-HP emergency hangup);
// consumed by MainWindowViewModel (to suppress the reactive
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
    // Game.Remote.HangupHandler and Game.Health.HealthManager's emergency-hangup
    // path.
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

    // Clear the suppress-entry flag outright (no consume-as-return semantics).
    // Called when an INVOLUNTARY host-side drop is classified (carrier lost /
    // keepalive timeout): a server disconnect the user didn't ask for must
    // auto-enter on its reconnect, so any stale suppress-entry intent left over
    // from an earlier deliberate hangup can't carry across the drop and block
    // re-entry. The deliberate paths (remote/user/low-HP hangup) never route
    // through the server-drop branch, so this leaves their suppression intact.
    public void AllowNextEntry() => _suppressNextEntry = false;

    // Clear both one-shot flags outright, reporting whether either was actually
    // set. Called on a profile swap: the hangup intent (suppress reconnect +
    // suppress next realm-entry) belongs to the character that hung up, and must
    // not carry into a DIFFERENT character's session. A stale suppress-entry
    // flag left by character A's low-HP / manual hangup otherwise strands
    // character B at the realm menu on its next connect, because
    // MainMenuEntryAutomation.Arm consumes it and refuses to auto-enter
    // (paradigm-20260909-172633). A same-character reconnect does NOT fire
    // ProfileLoaded, so this leaves the intentional "read the screen, enter
    // manually" behavior intact for the character that actually hung up.
    public bool Reset()
    {
        bool wasSet = _disconnectExpected || _suppressNextEntry;
        _disconnectExpected = false;
        _suppressNextEntry = false;
        return wasSet;
    }
}
