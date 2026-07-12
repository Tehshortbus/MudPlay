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
// One writer per app session — instantiated once in AppServices.
public sealed class ProgramLogFile : IAsyncDisposable
{
    private readonly LogService _log;
    private readonly DebugLogWriter _writer;
    private bool _broken;

    public ProgramLogFile(LogService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _writer = new DebugLogWriter("program");
        _log.EntryAdded += OnEntryAdded;
    }

    // Full path of the on-disk program log for this session.
    public string Path => _writer.Path;

    // EntryAdded fires on the producer's thread (possibly the Telnet read
    // loop); DebugLogWriter is internally locked, so no marshalling is needed.
    private void OnEntryAdded(LogEntry entry)
    {
        if (_broken) return;
        try
        {
            _writer.WriteLine($"[{entry.Severity}] {entry.Source}: {entry.Message}");
        }
        catch
        {
            // Disk full / handle lost / permission flap. Stop writing and stay
            // silent — logging the failure would re-enter EntryAdded and could
            // loop. Losing the trail is acceptable; wedging the log path is not.
            _broken = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _log.EntryAdded -= OnEntryAdded;
        await _writer.DisposeAsync();
    }
}
