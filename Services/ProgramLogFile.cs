namespace FujinTerm.Services;

// Tees the in-memory program log (LogService) to a rolling on-disk file so a
// hard lockup or a kill -9 — where no clean crash handler ever runs — still
// leaves a post-mortem trail. LogService alone keeps only its ring buffer in
// memory, which evaporates the moment the process dies; the crash reporter
// only fires on a managed exception, not on a hang. Every LogService entry is
// appended and flushed to disk immediately (DebugLogWriter runs AutoFlush), so
// the tail on disk survives even an unclean exit.
//
// The file lands under Data/Logs/ as {timestamp}-program.log and is covered by
// the same DebugLogWriter.PruneOldLogs retention sweep as every other .log.
// One instance per app session — instantiated once in AppServices.
//
// Gated by LogDiagnosticState.AutoCollectLogs: the writer only opens while that
// flag is on. Off (the default) means no file is created at all; flipping the
// Log-pane "Auto-collect logs" toggle on opens a fresh file, off closes it.
// The trade-off is deliberate — a default-off collector keeps normal sessions
// from littering Data/Logs, at the cost of the post-mortem trail unless the
// user opts in.
public sealed class ProgramLogFile : IAsyncDisposable
{
    private readonly LogService _log;
    private readonly LogDiagnosticState _diagnostics;

    // Guards the writer reference against the producer thread (EntryAdded fires
    // on the Telnet read loop) racing a toggle-driven open/close.
    private readonly object _gate = new();
    private DebugLogWriter? _writer;
    private bool _broken;

    public ProgramLogFile(LogService log, LogDiagnosticState diagnostics)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _log.EntryAdded += OnEntryAdded;
        _diagnostics.Changed += OnDiagnosticsChanged;
        SyncWriter();
    }

    // True while the on-disk file is open and receiving entries.
    public bool IsCollecting
    {
        get { lock (_gate) { return _writer is not null; } }
    }

    // Open or close the writer to match AutoCollectLogs. Once _broken (an IO
    // failure surfaced mid-session) we stay closed for the rest of the session
    // rather than thrash a failing handle.
    private void SyncWriter()
    {
        lock (_gate)
        {
            if (_broken) return;
            bool want = _diagnostics.AutoCollectLogs;
            if (want && _writer is null)
            {
                try { _writer = new DebugLogWriter("program"); }
                catch { _broken = true; }
            }
            else if (!want && _writer is not null)
            {
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    private void OnDiagnosticsChanged() => SyncWriter();

    // EntryAdded fires on the producer's thread (possibly the Telnet read
    // loop); the _gate lock guards the writer swap, so no marshalling is
    // needed.
    private void OnEntryAdded(LogEntry entry)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                _writer.WriteLine($"[{entry.Severity}] {entry.Source}: {entry.Message}");
            }
            catch
            {
                // Disk full / handle lost / permission flap. Stop writing and
                // stay silent — logging the failure would re-enter EntryAdded
                // and could loop. Losing the trail is acceptable; wedging the
                // log path is not.
                _broken = true;
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _log.EntryAdded -= OnEntryAdded;
        _diagnostics.Changed -= OnDiagnosticsChanged;
        DebugLogWriter? writer;
        lock (_gate)
        {
            writer = _writer;
            _writer = null;
        }
        if (writer is not null) await writer.DisposeAsync();
    }
}
