namespace FujinTerm.Services;

// Per-session diagnostic file writer. Each instance opens one file under
// Data/Logs/{yyyy-MM-dd_HH-mm-ss}-{topic}.log and appends timestamped lines.
// Used by walk / loop / match diagnostics — anything too noisy for LogService
// but useful when chasing a bug.
//
// Thread-safe: a single internal lock guards the writer. Lines are flushed to
// disk on every WriteLine call so a crash doesn't truncate the tail.
//
// Rotation: callers invoke the static PruneOldLogs once at startup to delete
// files older than the configured retention window (default 30 days).
// Settings.Other exposes the knob.
public sealed class DebugLogWriter : IDisposable, IAsyncDisposable
{
    // Default retention window applied by PruneOldLogs.
    public const int DefaultRetentionDays = 30;

    private readonly object _gate = new();
    private StreamWriter? _writer;

    // Full path of the file this writer is appending to.
    public string Path { get; }

    // Topic tag from construction, surfaced for diagnostics.
    public string Topic { get; }

    // True until Dispose / DisposeAsync closes the file.
    public bool IsOpen
    {
        get { lock (_gate) { return _writer is not null; } }
    }

    // Open a fresh log file for topic. The path is derived from
    // AppPaths.NewDebugLogFile and includes a timestamp so concurrent writers
    // for the same topic don't collide.
    public DebugLogWriter(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        Topic = topic;
        Path = AppPaths.NewDebugLogFile(topic);

        FileStream stream = new(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        WriteHeader();
    }

    // Append message as a single line prefixed with a millisecond-precision
    // wall-clock timestamp. No-op once disposed.
    public void WriteLine(string message)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            _writer.Write(DateTimeOffset.Now.ToString("HH:mm:ss.fff"));
            _writer.Write(' ');
            _writer.WriteLine(message);
        }
    }

    // Append a formatted line. Shorthand for WriteLine(string.Format(...)).
    public void WriteLine(string format, params object?[] args) => WriteLine(string.Format(format, args));

    private void WriteHeader()
    {
        if (_writer is null) return;
        _writer.WriteLine($"# FujinTerm debug log — topic={Topic}");
        _writer.WriteLine($"# started {DateTimeOffset.Now:O}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // Delete every *.log file under AppPaths.LogsDir whose last-write time is
    // older than retentionDays. Called once on app startup. Failures on
    // individual files are swallowed so a single locked file can't break
    // startup. Returns the number of files actually deleted.
    public static int PruneOldLogs(int retentionDays = DefaultRetentionDays)
    {
        if (retentionDays <= 0) return 0;
        if (!Directory.Exists(AppPaths.LogsDir)) return 0;

        DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
        int deleted = 0;

        foreach (string file in Directory.EnumerateFiles(AppPaths.LogsDir, "*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTime(file) >= cutoff) continue;
                File.Delete(file);
                deleted++;
            }
            catch (IOException)
            {
                // File locked by another process (e.g., a live writer from a
                // second instance). Skip — next launch will try again.
            }
            catch (UnauthorizedAccessException)
            {
                // Permission flap. Same handling as IOException.
            }
        }

        return deleted;
    }
}
