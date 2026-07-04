using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// Sends the configured GameCommands.EntryCommand (default `E`) when the
// MajorMUD main-menu screen is recognised at the tail of the automated
// BBS-login sequence, then — ONLY once the first in-game room display is
// actually observed — follows it up with a two-step refresh sequence (`stat` →
// `i`) so subscribers like StatParser / InventoryManager seed PlayerStats +
// inventory state right at the moment we land in the realm. `exp` isn't sent:
// `stat` already carries Level + current Exp, and the per-level derived fields
// (exp-to-next / level span / percent) come from the exp-table calc — the
// player can still type `exp` manually any time.
//
// Security model: the entry command must NEVER auto-fire on a chat line that
// happens to look like the main menu (a malicious player could gossip or
// telepath `[E] . Enter the Realm` to trick a naive client into auto-entering
// when the player wanted to stay out-of-realm). To prevent that, the engine is
// latched closed by default and is only briefly armed when the LoginAutomator
// reports its final step completed. The arm window has a short TTL (ArmWindow,
// default 15 s): if the menu doesn't appear in time, the latch closes and
// in-game lines can't trip it later.
//
// On a typical connect: TelnetClient connects → LoginAutomator walks the
// menu-nav sequence → final step's LoggedIntoGame event fires → main-window VM
// calls Arm → the next MainMenuEnterRealm pattern match (the actual menu screen)
// sends the entry command + closes the latch + arms the room-display gate; the
// startup refresh fires only when the first in-game room display appears.
// Mid-session navigation to menu (user types X to exit realm) doesn't re-arm —
// only a fresh login automation completion does.
//
// Hangup suppression: if a HangupSignal.SignalHangup fired before the current
// connect (intentional `@hangup` or a hang-up-if-naked / hang-up-if-low-HP
// automation), Arm consumes the suppression flag and refuses to open the latch
// — user reads what's on the screen and types their entry manually so the
// auto-refresh doesn't spam the wire while they're in a dangerous spot.
//
// Game-entry gate: after the entry command goes out we send NOTHING else until
// the first `Obvious exits:` line proves we actually landed in the realm
// (KnownPatterns.RoomExits). If the MOTD pauses on a "press any key"
// pagination, or the character is bounced / dead / held out-of-realm, no room
// display appears and the refresh never fires — the user advances manually and
// types their own commands. The wait is open-ended: it holds until the first
// room is finally seen (or a fresh login resets the gate), then fires once and
// latches closed.
//
// Startup sequence cadence: the two lines (`stat\r`, `i\r`) are sent with
// StartupStep gaps (default 400 ms) so the BBS renders each response cleanly
// before the next command lands. `stat` is parsed by StatParser; `i` is parsed
// by the inventory work.
public sealed class MainMenuEntryAutomation : IDisposable
{
    private readonly MessageRouter _router;
    private readonly GameCommands _commands;
    private readonly HangupSignal _hangup;
    private readonly Func<bool> _isAutoEnabled;
    private readonly LogService? _log;
    private readonly IDisposable _patternSub;
    private readonly IDisposable _roomSub;
    private readonly WireSender _wire = new();
    private DateTime _armedUntilUtc = DateTime.MinValue;
    private Avalonia.Threading.DispatcherTimer? _startupTimer;
    private int _startupIndex;
    private bool _awaitingFirstRoom;
    private bool _disposed;

    // Startup refresh sent once the first in-game room display confirms we
    // entered the realm. `stat` populates PlayerStats (Level + current Exp
    // included); `i` emits inventory text the InventoryManager parser consumes.
    // Read-only so tests + settings UI can introspect the canonical sequence
    // without rebuilding it.
    public static readonly IReadOnlyList<string> StartupSequence =
        new[] { "stat", "i" };

    // How long the latch stays armed after Arm. Default 15 s — long enough for
    // the BBS to actually paint the main menu after login completes, short
    // enough that an in-game chat line arriving minutes later can't trip the
    // entry-command send.
    public TimeSpan ArmWindow { get; set; } = TimeSpan.FromSeconds(15);

    // Gap between successive startup-sequence sends after the entry command
    // lands. Default 400 ms — long enough for a small MajorMUD screen (the stat
    // block or inventory list) to scroll without the next command queueing into
    // the middle of the previous response, short enough that the commands
    // complete within ~1.2 s of entering the realm.
    public TimeSpan StartupStep { get; set; } = TimeSpan.FromMilliseconds(400);

    // Test seam — override the clock so unit tests don't have to sleep.
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    // Test seam — most recent bytes the engine asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // True when armed AND inside the window.
    public bool IsArmed => NowProvider() < _armedUntilUtc;

    public MainMenuEntryAutomation(
        MessageRouter router,
        GameCommands commands,
        HangupSignal hangup,
        Func<bool>? isAutoEnabled = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(hangup);
        _router = router;
        _commands = commands;
        _hangup = hangup;
        // Master auto-responses gate. When it returns false, auto-entry is
        // suppressed on every connect / post-cleanup relog. Defaults to
        // "always on" so tests + callers that don't pass it behave as before.
        _isAutoEnabled = isAutoEnabled ?? (() => true);
        _log = log;
        _patternSub = _router.Subscribe(KnownPatterns.MainMenuEnterRealm, OnMainMenuLine);
        // Game-entry confirmation: the first "Obvious exits:" line after
        // an entry-command send proves we're actually in the realm and
        // unlocks the stat/exp/i refresh. See OnRoomExitsLine.
        _roomSub = _router.Subscribe(KnownPatterns.RoomExits, OnRoomExitsLine);
    }

    // Bind the wire-sender. MainWindowViewModel supplies SendUserInput alongside
    // the other auto-send services. Without it the engine still observes pattern
    // matches but produces no wire output.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Open the arm window — UNLESS a hangup signal is pending, in which case
    // consume the suppression flag and refuse to arm so the user manually
    // re-enters. Called by MainWindowViewModel right after
    // LoginAutomator.LoggedIntoGame fires (the only point in the session where
    // auto-entering the realm is authorised). The window closes on first
    // menu-match OR when ArmWindow elapses, whichever first.
    public void Arm()
    {
        // Fresh login → clear any stale room-display gate from a prior
        // connect where the entry command fired but no room ever
        // appeared, so a late room line can't trip the refresh out of
        // band (and a hangup-suppressed Arm leaves nothing armed).
        _awaitingFirstRoom = false;
        if (_hangup.ConsumeSuppressEntry())
        {
            _log?.Log(LogSeverity.Info, "MainMenuEntry",
                "Skipping auto-entry — prior hangup intent; user types entry command manually.");
            return;
        }
        _armedUntilUtc = NowProvider() + ArmWindow;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _patternSub.Dispose();
        _roomSub.Dispose();
        StopStartupSequence();
    }

    // Test seam — runs one pass of the startup-sequence timer without a real
    // tick. Mirrors production: the timer only fires after StartStartupSequence
    // sets it up (which only happens after a successful entry-command send), so
    // a suppressed-entry / never-armed test path produces no startup commands.
    internal void TickStartupSequenceForTests()
    {
        if (_startupTimer is null) return;
        SendNextStartupCommand();
    }

    private void OnMainMenuLine(MatchResult _)
    {
        // Tight latch check: must be armed AND inside the window.
        // Once the entry command is sent we close the latch so a
        // subsequent in-game line that happens to look like the menu
        // (gossip, telepath, room description) can't re-fire.
        if (NowProvider() >= _armedUntilUtc) return;
        _armedUntilUtc = DateTime.MinValue;

        // Master gate: auto-responses off → no auto-entry, regardless of
        // first connect or relog after cleanup. The latch is already
        // consumed above, so a later toggle-on can't fire this stale match.
        if (!_isAutoEnabled())
        {
            _log?.Log(LogSeverity.Info, "MainMenuEntry",
                "Auto-responses off — skipping auto-entry; user types the entry command manually.");
            return;
        }

        string command = _commands.EntryCommand;
        if (string.IsNullOrEmpty(command)) return;
        if (!_wire.IsBound) return;

        _wire.Send(command);
        _log?.Log(LogSeverity.Info, "MainMenuEntry",
            $"Auto-entered realm with '{command}'; awaiting first room display before refresh.");
        // Don't fire stat/exp/i yet — wait until a room display proves
        // we actually landed in the realm. OnRoomExitsLine releases it.
        _awaitingFirstRoom = true;
    }

    private void OnRoomExitsLine(MatchResult _)
    {
        // One-shot gate: only the first "Obvious exits:" line following
        // a successful entry-command send unlocks the refresh. Rooms
        // seen during normal play (or before any entry) are ignored.
        if (!_awaitingFirstRoom) return;
        _awaitingFirstRoom = false;
        _log?.Log(LogSeverity.Info, "MainMenuEntry",
            "First in-game room observed — sending startup refresh (stat/i).");
        StartStartupSequence();
    }


    // ----- Post-entry startup sequence ----------------------------------

    private void StartStartupSequence()
    {
        StopStartupSequence();
        _startupIndex = 0;
        _startupTimer = new Avalonia.Threading.DispatcherTimer(
            interval: StartupStep,
            priority: Avalonia.Threading.DispatcherPriority.Background,
            callback: (_, _) => SendNextStartupCommand());
        _startupTimer.Start();
    }

    private void StopStartupSequence()
    {
        _startupTimer?.Stop();
        _startupTimer = null;
    }

    private void SendNextStartupCommand()
    {
        if (_startupIndex >= StartupSequence.Count)
        {
            StopStartupSequence();
            return;
        }
        string next = StartupSequence[_startupIndex];
        _startupIndex++;
        _wire.Send(next);
        _log?.Log(LogSeverity.Debug, "MainMenuEntry",
            $"Sent post-entry refresh: {next}.");
    }
}
