using MudPlay.Services;

namespace MudPlay.Game;

// Keeps the in-game statline aligned with the Settings → Statline editor for
// the whole session. The editor command is the single source of truth: it's
// what we send to the BBS via set statline and what
// StatlinePromptRegexBuilder compiles the prompt parser from. When the live
// prompt doesn't match the editor's shape — the scanner fires
// PromptShapeUnmatched — this resends set statline <editor> so the editor
// always wins, never the other way around.
//
// Armed unconditionally on every Connected — unlike the auto-login and
// main-menu engines, which gate on the user having configured automation. The
// statline must reconcile whether or not login was automated. Verification
// always runs; the resend is mismatch-gated, so a session already on the
// editor's statline (persisted from a prior session) sends nothing.
//
// Default-statline users reconcile too (the resend is `set statline full`):
// a server whose class default isn't one of the stock shapes — or a statline
// the player set by hand in-game, even plain text with no numbers — leaves the
// Default parser reading nothing, so HP sits at 0 and every HP-gated engine
// stalls (report `stock-20260929-111956`).
//
// Mismatches only count once we're in the game (a room display has been seen
// since Arm) so BBS menus before login can never trigger a send, and only a
// run of MismatchThreshold of them in a row acts, so one garbled or stray
// bracketed line can't. Any matching prompt breaks the run.
//
// Reactive, not timer-driven: each confirmed run drives one reconcile attempt,
// paced by RetryDelay so the burst of in-flight prompts arriving between our
// send and the server applying it doesn't trigger duplicate sends. Bounded by
// MaxRetries so a statline the server won't take stops resending rather than
// hammering the wire. A matching prompt latches IsSynced and ends the run; a
// later run (the statline changed in-game mid-session) gets its own resends.
//
// When the resends run out and the prompt still doesn't match, the mismatch is
// flagged (FlagChanged) so the user is told — HP can't be read, and nothing
// else would say so. The flag outlives a reconnect and clears on the first
// matching prompt.
public sealed class StatlineReconciler : IDisposable
{
    private readonly WirePromptScanner _scanner;
    private readonly WireSender _wire = new();
    private readonly LogService? _log;
    private Func<string?> _desiredProvider = static () => null;

    private bool _armed;
    private bool _inGame;
    private bool _synced;
    private bool _gaveUp;
    private bool _mismatchLogged;
    private int _retries;
    private int _consecutiveMismatches;
    private DateTime _lastSendUtc = DateTime.MinValue;
    private bool _disposed;

    // Maximum resends before giving up, per arm. Default 3.
    public int MaxRetries { get; set; } = 3;

    // Unmatched prompts in a row (no matching prompt between) needed before a
    // resend or the give-up. Default 3.
    public int MismatchThreshold { get; set; } = 3;

    // Minimum gap between resends. Collapses the burst of stale prompts that
    // arrive between a set statline send and the server applying it, and paces
    // retries when a send doesn't take. Default 2 s.
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    private readonly Func<DateTime> _now;

    // Raised when IsFlagged flips either way. Fires on the prompt scanner's
    // thread, from inside its Append.
    public event Action? FlagChanged;

    // True between Arm and Disarm.
    public bool IsArmed => _armed;

    // True once a room display has been seen since Arm — the in-game gate.
    public bool IsInGame => _inGame;

    // True once a live prompt matched the editor's shape this connect.
    public bool IsSynced => _synced;

    // True once this arm's resends ran out with the prompt still unmatched.
    public bool HasGivenUp => _gaveUp;

    // Resends sent this arm.
    public int Retries => _retries;

    // Current run of unmatched prompts.
    public int ConsecutiveMismatches => _consecutiveMismatches;

    // True while the user is being warned that the live prompt can't be read.
    public bool IsFlagged => FlagNotice is not null;

    // The terminal notice for the current flag; null when not flagged.
    public string? FlagNotice { get; private set; }

    // The most recent statline-shaped prompt the active pattern didn't match.
    public string? LastUnmatchedPrompt { get; private set; }

    // Whether the latest prompt seen matched the editor's statline; null until
    // a prompt has been seen.
    public bool? LastPromptMatched { get; private set; }

    // The editor's statline command as it stands now.
    public string? DesiredCommand => _desiredProvider();

    public StatlineReconciler(WirePromptScanner scanner, LogService? log = null, Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        ArgumentNullException.ThrowIfNull(scanner);
        _scanner = scanner;
        _log = log;
        _scanner.PromptObserved += OnPromptObserved;
        _scanner.PromptShapeUnmatched += OnPromptShapeUnmatched;
    }

    // Bind the (gate-wrapped) wire sender used to resend set statline.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Supply the editor's current statline command. Read at send time so the
    // latest saved value is what gets reasserted.
    public void SetDesiredCommandProvider(Func<string?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _desiredProvider = provider;
    }

    // Arm reconciliation for a fresh connect: reset the per-connect latches and
    // counters so verification re-runs from scratch. Called unconditionally on
    // every Connected. The flag is left alone — it describes the prompt, and
    // only a matching prompt proves it's fixed.
    public void Arm()
    {
        _armed = true;
        _inGame = false;
        _synced = false;
        _gaveUp = false;
        _mismatchLogged = false;
        _retries = 0;
        _consecutiveMismatches = 0;
        _lastSendUtc = DateTime.MinValue;
    }

    // Stop reconciling (on disconnect). The next Arm re-enables it.
    public void Disarm() => _armed = false;

    // A room display arrived — we're past the BBS menus and in the game, so
    // statline-shaped prompts from here on are the game's.
    public void NoteRoomDisplayed()
    {
        if (_armed) _inGame = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanner.PromptObserved -= OnPromptObserved;
        _scanner.PromptShapeUnmatched -= OnPromptShapeUnmatched;
    }

    private void OnPromptObserved(PromptObservation _)
    {
        LastPromptMatched = true;
        _consecutiveMismatches = 0;

        if (FlagNotice is not null)
        {
            FlagNotice = null;
            _log?.Log(LogSeverity.Info, "Statline",
                "The game's prompt matches Settings -> Statline again - mismatch warning cleared.");
            FlagChanged?.Invoke();
        }

        // A live prompt matched the active (editor-built) pattern → the game is
        // on the editor's statline, and any mismatch run is over. A later one (the
        // statline changed in-game mid-session) starts afresh with its own resends.
        if (!_armed || _synced) return;
        _synced = true;
        if (_retries > 0)
            _log?.Log(LogSeverity.Info, "Statline",
                $"Statline reset worked - the game's prompt now matches Settings -> Statline (after {_retries} resend(s)).");
        _retries = 0;
        _gaveUp = false;
        _mismatchLogged = false;
    }

    private void OnPromptShapeUnmatched(string prompt)
    {
        LastPromptMatched = false;
        LastUnmatchedPrompt = prompt;

        if (!_armed || _gaveUp || !_inGame) return;
        _synced = false;

        _consecutiveMismatches++;
        if (_consecutiveMismatches < MismatchThreshold) return;

        // Pace resends: collapse the in-flight stale-prompt burst and space out
        // retries while the server catches up. The run keeps counting, so the
        // next unmatched prompt past the delay acts.
        if (_now() - _lastSendUtc < RetryDelay) return;

        string? desired = _desiredProvider();
        if (!_mismatchLogged)
        {
            _mismatchLogged = true;
            _log?.Log(LogSeverity.Info, "Statline",
                $"Statline mismatch: the game's prompt \"{prompt}\" doesn't match Settings -> Statline "
                + $"({_consecutiveMismatches} prompts in a row) - HP and MA can't be read.");
        }

        if (_retries >= MaxRetries)
        {
            GiveUp(prompt, desired);
            return;
        }

        if (!_wire.IsBound) return;

        string wire = StatlineSyntax.NormalizeForWire(desired ?? StatlineSyntax.Default);
        _wire.Send($"set statline {wire}");
        _retries++;
        _consecutiveMismatches = 0;
        _lastSendUtc = _now();
        _log?.Log(LogSeverity.Info, "Statline",
            $"Reset the game's statline to Settings -> Statline - sent `set statline {wire}` (attempt {_retries}/{MaxRetries}).");
    }

    private void GiveUp(string prompt, string? desired)
    {
        _gaveUp = true;
        bool alreadyWarned = FlagNotice is not null;
        _log?.Log(LogSeverity.Warn, "Statline",
            $"Gave up reconciling statline after {MaxRetries} resend(s) - the game's prompt \"{prompt}\" "
            + "still doesn't match Settings -> Statline"
            + (alreadyWarned ? " (mismatch warning already showing)." : " - showing the mismatch warning."));
        if (alreadyWarned) return;   // one notice until a matching prompt clears it
        FlagNotice = BuildNotice(prompt, desired);
        FlagChanged?.Invoke();
    }

    // The terminal notice for a flagged mismatch. Plain ASCII — the terminal
    // decodes CP437. A Default editor can be fixed by putting the game back on
    // its class default; a custom editor that the game refused to take is
    // fixed by editing it, or by falling back to Default.
    public static string BuildNotice(string livePrompt, string? editorCommand)
    {
        string remedy = StatlineSyntax.IsDefault(editorCommand)
            ? "or type: set statline full"
            : "or set it back to Default";
        return $"[ Statline mismatch: the game's prompt \"{livePrompt}\" doesn't match Settings -> Statline, "
            + "so HP and MA can't be read and automation sees 0 HP. "
            + $"Set Settings -> Statline to match the game, {remedy} ]";
    }
}
