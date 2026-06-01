namespace FujinTerm.Services;

/// <summary>
/// Application-wide log ring buffer with severity tags. Producers call
/// <see cref="Log"/> (or one of the per-severity shorthands); consumers
/// subscribe to <see cref="EntryAdded"/> or snapshot the buffer via
/// <see cref="Snapshot"/>. Phase 1's <c>LogPaneWindow</c> is the primary
/// consumer; the status bar binds to <see cref="Latest"/>.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safety: <see cref="Log"/> may be called from any thread (e.g., the
/// Telnet read loop). The ring buffer is guarded by an internal lock; the
/// <see cref="EntryAdded"/> event is fired <i>after</i> the lock is released,
/// on the calling thread. Subscribers that touch UI state must marshal via
/// <c>Dispatcher.UIThread.Post</c> themselves.
/// </para>
/// <para>
/// Capacity defaults to 2000 entries; oldest entries are overwritten on
/// wraparound. Settings.Display will expose the size knob in Phase 4; for
/// Phase 0 it's a constructor argument.
/// </para>
/// </remarks>
public sealed class LogService
{
    /// <summary>Default ring-buffer capacity. Overridable via the constructor.</summary>
    public const int DefaultCapacity = 2000;

    private readonly object _gate = new();
    private readonly LogEntry[] _ring;
    private int _head;          // next write slot
    private int _count;         // live entries in the ring (≤ Capacity)
    private LogEntry? _latest;

    /// <summary>Fired after each successful <see cref="Log"/> call, on the producer's thread.</summary>
    public event Action<LogEntry>? EntryAdded;

    /// <summary>
    /// Per-source double-click handler registry. The LogPane looks
    /// up the handler by an entry's <see cref="LogEntry.Source"/>
    /// and invokes it when the user double-clicks the row — lets a
    /// service like <c>SpellCoverageAuditor</c> register
    /// <c>"GameData/Coverage"</c> + open a detail window without the
    /// LogPane needing to know what coverage even is. Keys are
    /// matched ordinal-case-sensitive (same convention as the
    /// entry's Source tag).
    /// </summary>
    private readonly Dictionary<string, Action> _detailHandlers = new(StringComparer.Ordinal);

    /// <summary>
    /// Register a double-click handler for entries whose
    /// <see cref="LogEntry.Source"/> matches <paramref name="source"/>.
    /// Replaces any prior handler for that source (last one wins).
    /// Idempotent at startup time.
    /// </summary>
    public void RegisterDetailHandler(string source, Action handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_detailHandlers) { _detailHandlers[source] = handler; }
    }

    /// <summary>
    /// Invoke the detail handler for <paramref name="source"/>, or
    /// return <c>false</c> if no handler is registered. The LogPane's
    /// double-click handler is the only caller today.
    /// </summary>
    public bool TryInvokeDetailHandler(string source)
    {
        if (string.IsNullOrEmpty(source)) return false;
        Action? handler;
        lock (_detailHandlers)
        {
            if (!_detailHandlers.TryGetValue(source, out handler)) return false;
        }
        handler();
        return true;
    }

    /// <summary>Maximum number of entries retained.</summary>
    public int Capacity { get; }

    /// <summary>Most recently appended entry, or <c>null</c> if nothing has been logged yet.</summary>
    public LogEntry? Latest
    {
        get { lock (_gate) { return _latest; } }
    }

    public LogService(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _ring = new LogEntry[capacity];
    }

    /// <summary>Append an entry. Stamps <see cref="LogEntry.Timestamp"/> at call time.</summary>
    public void Log(LogSeverity severity, string source, string message)
    {
        LogEntry entry = new(DateTimeOffset.Now, severity, source ?? string.Empty, message ?? string.Empty);

        lock (_gate)
        {
            _ring[_head] = entry;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
            _latest = entry;
        }

        EntryAdded?.Invoke(entry);
    }

    /// <summary>
    /// Copy the live entries out in oldest-to-newest order. Returns a fresh
    /// array; safe for the caller to retain.
    /// </summary>
    public LogEntry[] Snapshot()
    {
        lock (_gate)
        {
            LogEntry[] result = new LogEntry[_count];
            if (_count == 0) return result;

            // Live region starts at (_head - _count) mod Capacity and runs _count entries forward.
            int start = (_head - _count + Capacity) % Capacity;
            int firstRun = Math.Min(_count, Capacity - start);
            Array.Copy(_ring, start, result, 0, firstRun);
            if (firstRun < _count)
            {
                Array.Copy(_ring, 0, result, firstRun, _count - firstRun);
            }
            return result;
        }
    }

    /// <summary>Drop every entry. Does not fire <see cref="EntryAdded"/>.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
            _latest = null;
        }
    }

    // ----- Convenience shorthands ---------------------------------------

    public void Debug(string source, string message)   => Log(LogSeverity.Debug,   source, message);
    public void Info(string source, string message)    => Log(LogSeverity.Info,    source, message);
    public void Warn(string source, string message)    => Log(LogSeverity.Warn,    source, message);
    public void Error(string source, string message)   => Log(LogSeverity.Error,   source, message);
    public void GameMsg(string source, string message) => Log(LogSeverity.GameMsg, source, message);
    public void Cmd(string source, string message)     => Log(LogSeverity.Cmd,     source, message);
}
