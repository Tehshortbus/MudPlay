using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// Drives the proactive log-off half of the nightly-cleanup cycle. When the BBS
// announces an upcoming shutdown (CleanupWarningWatcher.WarningObserved), this
// engine waits until the character is somewhere safe, exits the realm back to
// MajorMUD's main menu, then drops the carrier — leaving the predictive
// reconnect scheduler in MainWindowViewModel.TryScheduleCleanupReconnect to
// redial once the BBS is expected to be back online.
//
// Sequence: warning → wait-for-safe → send the exit command (default `x`) →
// wait until we're demonstrably out of the realm → disconnect. We deliberately
// drop the carrier ourselves rather than asking the BBS to hang us up (`=x`) —
// the user wants a clean local disconnect once we're back out of the realm.
//
// "Out of the realm" is confirmed primarily by MajorMUD's realm-exit line
// "Your character has been saved." (KnownPatterns.RealmExitSaved) — board-
// agnostic game text that fires the moment the character is saved, before any
// BBS door/games menu renders. The old trigger — the "[E] Enter the Realm"
// main-menu row — only appears on boards that drop you straight back to
// MajorMUD's entry menu; boards that nest the realm under extra door/games
// menus exit past it entirely (report stock-20260904-230111: exiting landed on
// the door post-game screen then the BBS games menu, neither of which shows
// that row, so the disconnect only ever came from the blind timeout below).
// Both the saved line and the main-menu row still complete the logout, and so
// does the Stock engine's own out-of-the-game prompt, "[MAJORMUD]: "
// (NoteRealmLeftPrompt): the saved sentence is most likely a text block the board
// can reword, the prompt after it is fixed in the engine. The double-timeout is
// the last-ditch fallback if none is seen.
//
// The game can call the exit wait off ("Your meditation has been interrupted - you
// may not exit now!", KnownPatterns.RealmExitInterrupted; GAME_MECHANICS "Realm
// exit / logoff sequence"): an attack does it, and on Stock so does a typed
// `break`. The character is then still in the game, most likely with something
// swinging at it, so the engine goes back to waiting for a safe room, and that
// room has to read safe for a whole combat round before the exit goes out again:
// an attacker swings once a round, and each exit sent ends a fight in progress
// (`*Combat Off*` on Stock). The safe check can't see every attacker (another
// player, a monster set not to be attacked, one unseen in the dark), so the
// retries are capped; past the cap the exit stands and the timeouts below decide,
// as they do when no confirmation is seen at all.
//
// "Safe" is supplied by the host via SetSafePredicate — in production it's "no
// engageable hostiles in the room AND not mid-combat"
// (CombatStateTracker.HasEngageableHostiles + PlayerState.InCombat). The engine
// polls the predicate on a SafePollInterval timer so it logs off the moment the
// current fight clears, not before.
//
// Latch: one logout per cleanup cycle. The watcher fires a fresh CleanupWarning
// at every threshold (20m → 5m → 2m); only the first transitions us out of
// Idle. Reset (called on every connect) re-opens the latch for the next
// session.
//
// Gating: the whole flow is opt-in behind the active BBS's
// ReconnectAfterCleanup toggle (queried via SetAutoLogoutEnabledCheck). That
// switch already means "manage the cleanup cycle for me" — auto-logging-off
// before the BBS yanks us is the front half of the same cycle whose back half
// (auto-redial) it already governs. That per-BBS opt-in deliberately overrides
// the character's master "Disable hangups" kill-switch: a user who asked us to
// manage the cleanup cycle wants the graceful exit even though it ends in a
// carrier drop, so — unlike the panic @hangup / low-HP-emergency paths — this
// log-off is exempt from DisableHangups. Without the exemption a party member
// with the kill-switch on lingers in the realm while the rest of the party
// exits, then gets yanked by the BBS at the worst moment.
//
// Sending `x` to reach the menu mid-session does NOT re-trigger
// MainMenuEntryAutomation's auto-entry: that engine's latch is closed except in
// the ~15s after login automation completes, so our menu visit can't bounce us
// straight back into the realm.
public sealed class CleanupLogoutOrchestrator : IDisposable
{
    private readonly CleanupWarningWatcher _cleanup;
    private readonly MessageRouter _router;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    private readonly IDisposable _menuSub;
    private readonly IDisposable _savedSub;
    private readonly IDisposable _interruptedSub;

    // How many times the game has called our exit off this cycle, whether the next
    // exit owes the settle below, and since when the room has read safe for it.
    private int _interruptions;
    private bool _settleOwed;
    private DateTime? _safeSinceUtc;

    private Func<bool>? _isSafe;
    private Func<bool>? _isConnected;
    private Func<bool>? _autoLogoutEnabled;
    private Action? _disconnect;

    // Arms one safe-poll tick after the delay. A tick from a poll since stopped
    // finds _pollGeneration moved on and does nothing.
    private readonly Action<TimeSpan, Action> _schedulePoll;
    private int _pollGeneration;
    private DateTime _exitSentUtc;
    private bool _exitRetried;

    // For a bug report: the cleanup log-off in progress, or null.
    public string? DescribePending() =>
        !_settleOwed && _safeSinceUtc is null && _exitSentUtc == default ? null
        : (_settleOwed ? "a settle is owed" : "no settle owed")
          + (_safeSinceUtc is { } safe ? $", safe since {safe.ToLocalTime():HH:mm:ss}" : string.Empty)
          + (_exitSentUtc != default ? $", exit sent {_exitSentUtc.ToLocalTime():HH:mm:ss}{(_exitRetried ? " (retried)" : string.Empty)}" : string.Empty);
    private bool _disposed;

    // Where in the log-off sequence we are. Public so the host can
    // observe the FSM without driving it.
    public CleanupLogoutPhase Phase { get; private set; } = CleanupLogoutPhase.Idle;

    // How often we re-check the safe predicate while waiting to log off. Default
    // 2s — fast enough that we exit within a round or two of the room clearing,
    // slow enough not to busy-poll.
    public TimeSpan SafePollInterval { get; set; } = TimeSpan.FromSeconds(2);

    // How long we wait for the main-menu screen after sending the exit command
    // before re-sending it once, then (on a second timeout) dropping the carrier
    // anyway. Default 15s — generous for a slow MOTD / "press any key" pause
    // between the realm and the menu.
    public TimeSpan MenuWaitTimeout { get; set; } = TimeSpan.FromSeconds(15);

    // After the game called an exit off, how long the room must read safe without
    // a break before the exit is sent again: one combat round, the longest an
    // attacker goes between swings.
    public TimeSpan InterruptSettle { get; set; } = TickEngine.CombatTickInterval;

    // How many called-off exits are answered with another try. At this many the
    // room has read safe each time and something still stopped the exit, so the
    // safe check can't see it and trying again would only repeat the cycle.
    public int MaxInterruptions { get; set; } = 3;

    // The command that exits the realm to the main menu: the in-game `x`, so the
    // character is saved on the way out ("Your character has been saved.").
    private const string ExitCommand = "x";

    private readonly Func<DateTime> _now;

    public CleanupLogoutOrchestrator(
        CleanupWarningWatcher cleanup,
        MessageRouter router,
        LogService? log = null,
        Action<TimeSpan, Action>? schedulePoll = null,
        Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(router);
        _cleanup = cleanup;
        _router = router;
        _log = log;
        _schedulePoll = schedulePoll ?? ((delay, tick) =>
            Avalonia.Threading.DispatcherTimer.RunOnce(tick, delay, Avalonia.Threading.DispatcherPriority.Background));
        _cleanup.WarningObserved += OnWarningObserved;
        _menuSub = _router.Subscribe(KnownPatterns.MainMenuEnterRealm, OnMainMenuLine);
        _savedSub = _router.Subscribe(KnownPatterns.RealmExitSaved, OnRealmExitSaved);
        _interruptedSub = _router.Subscribe(KnownPatterns.RealmExitInterrupted, OnRealmExitInterrupted);
    }

    // Bind the wire sink used to send the exit command. The host supplies the
    // gate-wrapped engine sender (same one the walker uses) so the exit can't
    // blast through a password-entry prompt.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Supply the "is it safe to log off right now?" predicate (room clear of
    // killable NPCs + not mid-combat).
    public void SetSafePredicate(Func<bool> isSafe) => _isSafe = isSafe;

    // Supply the live connection check — aborts the flow if the wire drops
    // before we finish logging off.
    public void SetConnectedCheck(Func<bool> isConnected) => _isConnected = isConnected;

    // Supply the opt-in gate (active BBS's ReconnectAfterCleanup).
    public void SetAutoLogoutEnabledCheck(Func<bool> enabled) => _autoLogoutEnabled = enabled;

    // Supply the master switch (true = off). The cleanup log-off is not a
    // hang-up, but it is automatic: with the switch off it doesn't start, and one
    // still waiting for a safe room waits on (user, 2026-10-09). An exit already
    // sent runs to its end, or the character would be left at the menu. The
    // reconnect after the board's own drop is the BBS settings' business, not ours.
    public void SetMasterSwitchCheck(Func<bool> isOff) => _masterSwitchOff = isOff;

    private Func<bool>? _masterSwitchOff;

    // Supply the disconnect action invoked once we reach the main menu (the
    // host's user-initiated disconnect path).
    public void SetDisconnectCallback(Action disconnect) => _disconnect = disconnect;

    // Re-open the latch + stop any in-flight wait. Called on every connect so
    // the next session's first warning arms cleanly.
    public void Reset()
    {
        StopTimer();
        Phase = CleanupLogoutPhase.Idle;
        _exitRetried = false;
        _interruptions = 0;
        _settleOwed = false;
        _safeSinceUtc = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanup.WarningObserved -= OnWarningObserved;
        _menuSub.Dispose();
        _savedSub.Dispose();
        _interruptedSub.Dispose();
        StopTimer();
    }

    private void OnWarningObserved(CleanupWarning warning)
    {
        // One logout per cleanup cycle — ignore the 5m / 2m follow-ups
        // once the first warning has armed us.
        if (Phase != CleanupLogoutPhase.Idle) return;
        if (!(_autoLogoutEnabled?.Invoke() ?? false)) return;
        if (!(_isConnected?.Invoke() ?? false)) return;
        // Left Idle, so a later warning of the same cycle (5m, 2m) arms the
        // log-off if the switch is back on by then.
        if (_masterSwitchOff?.Invoke() == true)
        {
            _log?.Log(LogSeverity.Info, "CleanupLogout",
                $"Shutdown warning ({warning.MinutesRemaining}m) — the master switch is off, so no automatic log-off.");
            return;
        }

        Phase = CleanupLogoutPhase.Pending;
        _log?.Log(LogSeverity.Info, "CleanupLogout",
            $"Shutdown warning ({warning.MinutesRemaining}m) — waiting for a safe room before logging off.");
        StartTimer();
        Evaluate(); // try immediately — the room may already be clear.
    }

    private void OnMainMenuLine(MatchResult _)
    {
        // Only meaningful once we've sent the exit and are waiting for the
        // menu to confirm we're out of the realm. The menu pattern at any
        // other time (login entry, manual mid-session exit) is not ours.
        if (Phase != CleanupLogoutPhase.Exiting) return;
        _log?.Log(LogSeverity.Info, "CleanupLogout",
            "Main menu reached — dropping carrier; auto-reconnect redials after cleanup.");
        CompleteLogout();
    }

    private void OnRealmExitSaved(MatchResult _)
    {
        // Primary "we're safely out" signal — MajorMUD saved the character on
        // exit. Gated to Exiting so it only counts once we've sent our own exit
        // command (never a stray save line during play). Board-agnostic: fires
        // before any door/BBS menu renders, so it works where the main-menu row
        // never appears.
        if (Phase != CleanupLogoutPhase.Exiting) return;
        _log?.Log(LogSeverity.Info, "CleanupLogout",
            "Character saved — out of the realm; dropping carrier, auto-reconnect redials after cleanup.");
        CompleteLogout();
    }

    // The engine's out-of-the-game prompt arrived (WirePromptScanner, which sees it
    // at a prompt position only). It is no line, so it reaches us from the wire
    // scanner and not the router. Counts only while our own exit is out: the same
    // prompt stands on a board's menus at other times.
    public void NoteRealmLeftPrompt()
    {
        if (Phase != CleanupLogoutPhase.Exiting) return;
        _log?.Log(LogSeverity.Info, "CleanupLogout",
            "The game's own menu prompt is showing — out of the realm; dropping carrier, auto-reconnect redials after cleanup.");
        CompleteLogout();
    }

    private void OnRealmExitInterrupted(MatchResult _)
    {
        // Ours only while our own exit is counting out; the same line answers a
        // wait the user started by hand.
        if (Phase != CleanupLogoutPhase.Exiting) return;
        _interruptions++;
        if (_interruptions >= MaxInterruptions)
        {
            if (_interruptions == MaxInterruptions)
                _log?.Log(LogSeverity.Warn, "CleanupLogout",
                    $"The exit was called off {_interruptions} times with the room reading safe each time — "
                    + "whatever stops it can't be seen from here. Not waiting again: the exit is re-sent "
                    + "once on the timeout and the carrier dropped after that.");
            return;
        }

        Phase = CleanupLogoutPhase.Pending;
        _settleOwed = true;
        _safeSinceUtc = null;
        _log?.Log(LogSeverity.Info, "CleanupLogout",
            $"The exit was called off — exiting again once the room has read safe for {InterruptSettle.TotalSeconds:0} s.");
    }

    // Single FSM step. Runs on the safe-poll timer and once inline when the
    // warning first arms us.
    private void Evaluate()
    {
        if (_disposed) return;

        // Lost the wire mid-flow — nothing left to log off from. The
        // reactive/predictive reconnect schedulers in the host handle
        // dialing back; we just stand down.
        if (!(_isConnected?.Invoke() ?? false))
        {
            Abort("connection dropped before logout completed");
            return;
        }

        switch (Phase)
        {
            case CleanupLogoutPhase.Pending:
                if (_masterSwitchOff?.Invoke() == true || !(_isSafe?.Invoke() ?? false))
                {
                    _safeSinceUtc = null;
                    break;
                }
                if (_settleOwed)
                {
                    _safeSinceUtc ??= _now();
                    if (_now() - _safeSinceUtc.Value < InterruptSettle) break;
                }
                BeginExit();
                break;

            case CleanupLogoutPhase.Exiting:
                if (_now() - _exitSentUtc < MenuWaitTimeout) break;
                if (!_exitRetried)
                {
                    _exitRetried = true;
                    SendExit("main menu not seen within timeout — re-sending exit");
                }
                else
                {
                    _log?.Log(LogSeverity.Warn, "CleanupLogout",
                        "Main menu never appeared after exit retry — dropping carrier anyway.");
                    CompleteLogout();
                }
                break;
        }
    }

    private void BeginExit()
    {
        Phase = CleanupLogoutPhase.Exiting;
        _exitRetried = false;
        _settleOwed = false;
        _safeSinceUtc = null;
        SendExit("room clear — sending exit to reach the main menu");
    }

    private void SendExit(string why)
    {
        _exitSentUtc = _now();
        _log?.Log(LogSeverity.Info, "CleanupLogout", why + $" ('{ExitCommand}').");
        if (_wire.IsBound) _wire.Send(ExitCommand);
    }

    private void CompleteLogout()
    {
        Phase = CleanupLogoutPhase.Done;
        StopTimer();
        _disconnect?.Invoke();
    }

    private void Abort(string why)
    {
        StopTimer();
        if (Phase is CleanupLogoutPhase.Idle or CleanupLogoutPhase.Done) return;
        _log?.Log(LogSeverity.Debug, "CleanupLogout", $"Logout stood down — {why}.");
        Phase = CleanupLogoutPhase.Done;
    }

    private void StartTimer()
    {
        StopTimer();
        ArmPoll();
    }

    private void ArmPoll()
    {
        int generation = _pollGeneration;
        _schedulePoll(SafePollInterval, () =>
        {
            if (generation != _pollGeneration) return;
            Evaluate();
            if (generation == _pollGeneration) ArmPoll();
        });
    }

    private void StopTimer() => _pollGeneration++;
}

// Phases of the cleanup-driven log-off sequence.
public enum CleanupLogoutPhase
{
    // No active warning, or auto-logout disabled — engine idle.
    Idle,

    // Warning seen; waiting for a safe room before exiting.
    Pending,

    // Exit command sent; waiting for the main-menu screen.
    Exiting,

    // Logout finished (carrier dropped) or stood down.
    Done,
}
