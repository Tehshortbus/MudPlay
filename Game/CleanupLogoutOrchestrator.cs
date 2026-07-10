using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// Drives the proactive log-off half of the nightly-cleanup cycle. When the BBS
// announces an upcoming shutdown (CleanupWarningWatcher.WarningObserved), this
// engine waits until the character is somewhere safe, exits the realm back to
// MajorMUD's main menu, then drops the carrier — leaving the predictive
// reconnect scheduler in MainWindowViewModel.TryScheduleCleanupReconnect to
// redial once the BBS is expected to be back online.
//
// Sequence: warning → wait-for-safe → send the exit command (default `x`) →
// wait for the main-menu screen → disconnect. We deliberately drop the carrier
// ourselves at the menu rather than asking the BBS to hang us up (`=x`) — the
// user wants a clean local disconnect once we're back out of the realm.
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

    private Func<bool>? _isSafe;
    private Func<bool>? _isConnected;
    private Func<bool>? _autoLogoutEnabled;
    private Action? _disconnect;

    private Avalonia.Threading.DispatcherTimer? _tick;
    private DateTime _exitSentUtc;
    private bool _exitRetried;
    private bool _disposed;

    // Where in the log-off sequence we are. Public so the host (and tests) can
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

    // The command that exits the realm to the main menu. The user's MajorMUD
    // exits with a bare `x`.
    public string ExitCommand { get; set; } = "x";

    // Test seam — override the clock so unit tests don't sleep.
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    // Test seam — bytes the engine asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    public CleanupLogoutOrchestrator(
        CleanupWarningWatcher cleanup,
        MessageRouter router,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(router);
        _cleanup = cleanup;
        _router = router;
        _log = log;
        _cleanup.WarningObserved += OnWarningObserved;
        _menuSub = _router.Subscribe(KnownPatterns.MainMenuEnterRealm, OnMainMenuLine);
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanup.WarningObserved -= OnWarningObserved;
        _menuSub.Dispose();
        StopTimer();
    }

    private void OnWarningObserved(CleanupWarning warning)
    {
        // One logout per cleanup cycle — ignore the 5m / 2m follow-ups
        // once the first warning has armed us.
        if (Phase != CleanupLogoutPhase.Idle) return;
        if (!(_autoLogoutEnabled?.Invoke() ?? false)) return;
        if (!(_isConnected?.Invoke() ?? false)) return;

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
                if (_isSafe?.Invoke() ?? false) BeginExit();
                break;

            case CleanupLogoutPhase.Exiting:
                if (NowProvider() - _exitSentUtc < MenuWaitTimeout) break;
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
        SendExit("room clear — sending exit to reach the main menu");
    }

    private void SendExit(string why)
    {
        _exitSentUtc = NowProvider();
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
        _tick = new Avalonia.Threading.DispatcherTimer(
            interval: SafePollInterval,
            priority: Avalonia.Threading.DispatcherPriority.Background,
            callback: (_, _) => Evaluate());
        _tick.Start();
    }

    private void StopTimer()
    {
        _tick?.Stop();
        _tick = null;
    }

    // Test seam — run one FSM step without a real timer tick.
    internal void EvaluateForTests() => Evaluate();
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
