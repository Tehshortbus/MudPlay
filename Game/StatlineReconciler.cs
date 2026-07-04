using FujinTerm.Services;

namespace FujinTerm.Game;

// Keeps the in-game statline aligned with the Settings → Statline editor on
// every (re)connect. The editor command is the single source of truth: it's
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
// Default-statline users never resend: the scanner's active pattern IS the
// permissive default, so PromptShapeUnmatched is structurally unreachable for
// them. Only an authored custom statline can drift from the default the
// server falls back to (fresh character / server reset), which is exactly the
// mismatch this engine corrects.
//
// Reactive, not timer-driven: each live prompt that fails to match drives one
// reconcile attempt, paced by RetryDelay so the burst of in-flight default
// prompts arriving between our send and the server applying it doesn't
// trigger duplicate sends. Bounded by MaxRetries so a built pattern that can
// never match the live prompt stops resending rather than hammering the wire.
// The first matching prompt latches IsSynced and ends reconciliation until
// the next connect re-arms.
public sealed class StatlineReconciler : IDisposable
{
    private readonly WirePromptScanner _scanner;
    private readonly WireSender _wire = new();
    private readonly LogService? _log;
    private Func<string?> _desiredProvider = static () => null;

    private bool _armed;
    private bool _synced;
    private int _retries;
    private DateTime _lastSendUtc = DateTime.MinValue;
    private bool _disposed;

    // Maximum resends before giving up, per arm. Default 3.
    public int MaxRetries { get; set; } = 3;

    // Minimum gap between resends. Collapses the burst of default prompts that
    // arrive between a set statline send and the server applying it, and paces
    // retries when a send doesn't take. Default 2 s.
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    // Test seam — override the clock so tests don't have to sleep.
    public Func<DateTime> NowProvider { get; set; } = static () => DateTime.UtcNow;

    // Test seam — every buffer the engine has asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // True between Arm and Disarm.
    public bool IsArmed => _armed;

    // True once a live prompt matched the editor's shape this connect.
    public bool IsSynced => _synced;

    public StatlineReconciler(WirePromptScanner scanner, LogService? log = null)
    {
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

    // Arm reconciliation for a fresh connect: reset the Synced latch and retry
    // counter so verification re-runs from scratch. Called unconditionally on
    // every Connected.
    public void Arm()
    {
        _armed = true;
        _synced = false;
        _retries = 0;
        _lastSendUtc = DateTime.MinValue;
    }

    // Stop reconciling (on disconnect). The next Arm re-enables it.
    public void Disarm() => _armed = false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanner.PromptObserved -= OnPromptObserved;
        _scanner.PromptShapeUnmatched -= OnPromptShapeUnmatched;
    }

    private void OnPromptObserved(PromptObservation _)
    {
        // A live prompt matched the active (editor-built) pattern → the game is
        // already on the editor's statline. Latch synced; nothing to send.
        if (!_armed || _synced) return;
        _synced = true;
    }

    private void OnPromptShapeUnmatched()
    {
        // A default-shaped prompt the active pattern didn't match → the game
        // isn't on the editor's statline. Resend it (editor wins).
        if (!_armed || _synced) return;

        // Pace resends: collapse the in-flight default-prompt burst and space
        // out retries while the server catches up.
        if (NowProvider() - _lastSendUtc < RetryDelay) return;

        if (_retries >= MaxRetries)
        {
            _log?.Log(LogSeverity.Warn, "Statline",
                $"Gave up reconciling statline after {MaxRetries} resend(s) — live prompt never matched the editor's shape.");
            return;
        }

        string? desired = _desiredProvider();
        // A default editor can't reach here (active == default → no unmatched
        // event), but guard anyway so we never blast `set statline full`.
        if (StatlineSyntax.IsDefault(desired)) return;
        if (!_wire.IsBound) return;

        string wire = StatlineSyntax.NormalizeForWire(desired!);
        _wire.Send($"set statline {wire}");
        _retries++;
        _lastSendUtc = NowProvider();
        _log?.Log(LogSeverity.Info, "Statline",
            $"Live prompt didn't match editor statline — resent `set statline {wire}` (attempt {_retries}/{MaxRetries}).");
    }
}
