namespace FujinTerm.Services;

/// <summary>
/// Per-session diagnostic file writer. Each instance opens one file under
/// <c>Data/Logs/{yyyy-MM-dd_HH-mm-ss}-{topic}.log</c> and appends timestamped
/// lines. Used by walk / loop / match diagnostics in later phases — anything
/// too noisy for <see cref="LogService"/> but useful when chasing a bug.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe: a single internal lock guards the writer. Lines are flushed
/// to disk on every <see cref="WriteLine"/> call so a crash doesn't truncate
/// the tail.
/// </para>
/// <para>
/// Rotation: callers invoke the static <see cref="PruneOldLogs"/> once at
/// startup to delete files older than the configured retention window
/// (default 30 days). Phase 4's Settings.Other exposes the knob.
/// </para>
/// </remarks>
public sealed class DebugLogWriter : IDisposable, IAsyncDisposable
{
    /// <summary>Default retention window applied by <see cref="PruneOldLogs"/>.</summary>
    public const int DefaultRetentionDays = 30;

    private readonly object _gate = new();
    private StreamWriter? _writer;

    /// <summary>Full path of the file this writer is appending to.</summary>
    public string Path { get; }

    /// <summary>Topic tag from construction, surfaced for diagnostics.</summary>
    public string Topic { get; }

    /// <summary>True until <see cref="Dispose"/> / <see cref="DisposeAsync"/> closes the file.</summary>
    public bool IsOpen
    {
        get { lock (_gate) { return _writer is not null; } }
    }

    /// <summary>
    /// Open a fresh log file for <paramref name="topic"/>. The path is derived
    /// from <see cref="AppPaths.NewDebugLogFile"/> and includes a timestamp so
    /// concurrent writers for the same topic don't collide.
    /// </summary>
    public DebugLogWriter(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        Topic = topic;
        Path = AppPaths.NewDebugLogFile(topic);

        FileStream stream = new(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        WriteHeader();
    }

    /// <summary>
    /// Append <paramref name="message"/> as a single line prefixed with a
    /// millisecond-precision wall-clock timestamp. No-op once disposed.
    /// </summary>
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

    /// <summary>Append a formatted line. Shorthand for <c>WriteLine(string.Format(...))</c>.</summary>
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

    /// <summary>
    /// Delete every <c>*.log</c> file under <see cref="AppPaths.LogsDir"/>
    /// whose last-write time is older than <paramref name="retentionDays"/>.
    /// Called once on app startup. Failures on individual files are swallowed
    /// (returned in the result) so a single locked file can't break startup.
    /// </summary>
    /// <returns>The number of files actually deleted.</returns>
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
