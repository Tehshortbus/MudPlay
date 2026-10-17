using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

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
// latched closed by default and is only armed inside an active BBS-login window.
// The arm window has a short TTL (ArmWindow, default 15 s) that KeepArmed
// refreshes on each login-step advance; if the menu doesn't appear before the
// window lapses with no further login progress, the latch closes and in-game
// lines can't trip it later.
//
// Arming lifecycle: the main-window VM calls Arm() at the START of a login
// (when the LoginAutomator is started for a fresh connect / reconnect / cleanup
// relog), then KeepArmed() on every step the automator matches, and again on
// LoggedIntoGame. So the entry command fires when the realm-entry menu appears
// even if a trailing (mis-authored or MegaMUD-holdover) nav step never matches
// and LoggedIntoGame never comes — the reported "did not re-enter on
// disconnect" case. Arm() decides authorization ONCE per login by consuming the
// hangup-suppression flag (below): a login that follows a health/manual hangup
// is not authorized, and KeepArmed can't re-open it. Mid-session navigation to
// menu (user types X to exit realm) doesn't arm — only a fresh login does.
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
    private readonly LogService? _log;
    private readonly IDisposable _patternSub;
    private readonly IDisposable _roomSub;
    private readonly WireSender _wire = new();
    private Action? _suppressNextMove;
    private DateTime _armedUntilUtc = DateTime.MinValue;
    // Latched once per login by Arm(): true when this login is authorized to
    // auto-enter (not hangup-suppressed), gating KeepArmed's window refresh so a
    // hangup-suppressed login can't be re-armed by later step progress. Cleared
    // when a menu line is handled (one auto-entry per login).
    private bool _entryAuthorized;
    private IDisposable? _startupTimer;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private int _startupIndex;
    private bool _awaitingFirstRoom;

    // For a bug report: the game entry in progress, or null.
    public string? DescribePending() =>
        !_awaitingFirstRoom && _armedUntilUtc <= DateTime.UtcNow ? null
        : (_armedUntilUtc > DateTime.UtcNow ? $"armed until {_armedUntilUtc.ToLocalTime():HH:mm:ss}" : "not armed")
          + (_awaitingFirstRoom ? ", awaiting the first room" : string.Empty);
    private bool _disposed;

    // Startup refresh sent once the first in-game room display confirms we
    // entered the realm. `stat` populates PlayerStats (Level + current Exp
    // included); `i` emits inventory text the InventoryManager parser consumes.
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
    // the middle of the previous response, short enough that both complete
    // within about a second of entering the realm.
    public TimeSpan StartupStep { get; set; } = TimeSpan.FromMilliseconds(400);

    private readonly Func<DateTime> _now;

    // True when armed AND inside the window.
    public bool IsArmed => _now() < _armedUntilUtc;

    public MainMenuEntryAutomation(
        MessageRouter router,
        GameCommands commands,
        HangupSignal hangup,
        LogService? log = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(hangup);
        _router = router;
        _commands = commands;
        _hangup = hangup;
        _log = log;
        _schedule = schedule ?? DispatcherTicks.Once;
        _patternSub = _router.Subscribe(KnownPatterns.MainMenuEnterRealm, OnMainMenuLine);
        // Game-entry confirmation: the first "Obvious exits:" line after
        // an entry-command send proves we're actually in the realm and
        // unlocks the stat / i refresh. See OnRoomExitsLine.
        _roomSub = _router.Subscribe(KnownPatterns.RoomExits, OnRoomExitsLine);
    }

    // Bind the wire-sender. MainWindowViewModel supplies SendUserInput alongside
    // the other auto-send services. Without it the engine still observes pattern
    // matches but produces no wire output.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Bind the callback that tells the outbound-move observer to ignore the
    // realm-entry keystroke. The entry command (default "E") collides with
    // cardinal East and rides the same wire-observe path as manual movement;
    // firing this right before the send stops the observer fabricating an East
    // step that desyncs the freshly-hydrated login room.
    public void SetMoveSuppressor(Action suppressor) => _suppressNextMove = suppressor;

    // Open the arm window — UNLESS a hangup signal is pending, in which case
    // consume the suppression flag and refuse to arm so the user manually
    // re-enters. Called by MainWindowViewModel at the start of each login, the one
    // point that decides whether this connect may auto-enter; KeepArmed refreshes
    // the window as the login advances. The window closes on first menu-match OR
    // when ArmWindow elapses, whichever first.
    public void Arm()
    {
        // Fresh login → clear any stale room-display gate from a prior
        // connect where the entry command fired but no room ever
        // appeared, so a late room line can't trip the refresh out of
        // band (and a hangup-suppressed Arm leaves nothing armed).
        _awaitingFirstRoom = false;
        if (_hangup.ConsumeSuppressEntry())
        {
            _entryAuthorized = false;
            _log?.Log(LogSeverity.Info, "MainMenuEntry",
                "Skipping auto-entry — prior hangup intent; user types entry command manually.");
            return;
        }
        _entryAuthorized = true;
        _armedUntilUtc = _now() + ArmWindow;
        // Log the arm so a reconnect capture shows auto-entry was actually
        // primed. Its ABSENCE is the tell that this login wasn't authorized
        // (hangup-suppressed) or the automator never started, so Arm was never
        // called.
        _log?.Log(LogSeverity.Info, "MainMenuEntry",
            $"Armed auto-entry (window {ArmWindow.TotalSeconds:0}s) — will send '{_commands.EntryCommand}' on the next main-menu line.");
    }

    // Refresh the arm window while a login is still progressing (each matched
    // nav step, and on LoggedIntoGame). No-ops unless this login's Arm()
    // authorized entry — so a hangup-suppressed login stays closed no matter how
    // many steps advance. Keeps auto-entry primed across a slow multi-step login,
    // and — crucially — keeps it primed when a trailing holdover step never
    // matches, so the realm-entry menu still fires the entry command.
    public void KeepArmed()
    {
        if (_disposed || !_entryAuthorized) return;
        _armedUntilUtc = _now() + ArmWindow;
        _log?.Log(LogSeverity.Debug, "MainMenuEntry",
            "Re-armed auto-entry — login still progressing.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _patternSub.Dispose();
        _roomSub.Dispose();
        StopStartupSequence();
    }

    private void OnMainMenuLine(MatchResult _)
    {
        // Tight latch check: must be armed AND inside the window.
        // Once the entry command is sent we close the latch so a
        // subsequent in-game line that happens to look like the menu
        // (gossip, telepath, room description) can't re-fire.
        if (_now() >= _armedUntilUtc) return;
        _armedUntilUtc = DateTime.MinValue;
        // One auto-entry per login: close authorization so a later KeepArmed
        // (a trailing step advancing, or a second menu line) can't re-open the
        // latch after we've already sent (or deliberately skipped) the entry.
        _entryAuthorized = false;

        // No enable probe here, on purpose: entering the realm is the last step
        // of logging in, and login automation works whatever the master switch
        // (Auto-All) reads (user, 2026-10-09).
        string command = _commands.EntryCommand;
        if (string.IsNullOrEmpty(command)) return;
        if (!_wire.IsBound) return;

        // Menu selection, not a step — keep the observer from reading "E" as
        // East. Must precede the send: the wire path is synchronous, so the
        // observer sees this keystroke on the very next call.
        _suppressNextMove?.Invoke();
        _wire.Send(command);
        _log?.Log(LogSeverity.Info, "MainMenuEntry",
            $"Auto-entered realm with '{command}'; awaiting first room display before refresh.");
        // Don't fire stat / i yet — wait until a room display proves
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
        _startupTimer = _schedule(StartupStep, OnStartupTick);
    }

    // One step of the sequence: the next is armed before this one is sent.
    private void OnStartupTick()
    {
        _startupTimer = _schedule(StartupStep, OnStartupTick);
        SendNextStartupCommand();
    }

    private void StopStartupSequence()
    {
        _startupTimer?.Dispose();
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
