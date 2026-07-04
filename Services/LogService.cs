namespace FujinTerm.Services;

// Application-wide log ring buffer with severity tags. Producers call Log
// (or one of the per-severity shorthands); consumers subscribe to EntryAdded
// or snapshot the buffer via Snapshot. The LogPaneWindow is the primary
// consumer; the status bar binds to Latest.
//
// Thread-safety: Log may be called from any thread (e.g., the Telnet read
// loop). The ring buffer is guarded by an internal lock; the EntryAdded
// event is fired after the lock is released, on the calling thread.
// Subscribers that touch UI state must marshal via Dispatcher.UIThread.Post
// themselves.
//
// Capacity defaults to 2000 entries; oldest entries are overwritten on
// wraparound. The size knob is exposed in Settings.Display; the constructor
// takes the initial value.
public sealed class LogService
{
    // Default ring-buffer capacity. Overridable via the constructor.
    public const int DefaultCapacity = 2000;

    private readonly object _gate = new();
    private readonly LogEntry[] _ring;
    private int _head;          // next write slot
    private int _count;         // live entries in the ring (≤ Capacity)
    private LogEntry? _latest;

    // Fired after each successful Log call, on the producer's thread.
    public event Action<LogEntry>? EntryAdded;

    // Session diagnostic state gating generation of the Debug and Combat
    // channels. Null in unit tests that don't exercise gating — treated as
    // "all diagnostics off" so Debug / Combat no-op. The running app wires
    // AppServices.LogDiagnostics here so the LogPane toggles (persisted
    // per-character) gate emission at the source, not just at display.
    public LogDiagnosticState? Diagnostics { get; set; }

    // True when the Debug channel should be generated. Guard hot paths on
    // this before building an interpolated Debug message so the disabled path
    // stays allocation-free.
    public bool IsDebugEnabled => Diagnostics?.DebugDiagnostics ?? false;

    // True when the Combat channel should be generated. Guard hot paths on
    // this before building an interpolated Combat message so the disabled
    // path stays allocation-free.
    public bool IsCombatEnabled => Diagnostics?.CombatDiagnostics ?? false;

    // Per-source double-click handler registry. The LogPane looks up the
    // handler by an entry's LogEntry.Source and invokes it when the user
    // double-clicks the row — lets a service like SpellCoverageAuditor
    // register "GameData/Coverage" + open a detail window without the LogPane
    // needing to know what coverage even is. Keys are matched
    // ordinal-case-sensitive (same convention as the entry's Source tag).
    private readonly Dictionary<string, Action> _detailHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<LogEntry>> _entryHandlers = new(StringComparer.Ordinal);

    // Register a no-arg double-click handler for entries whose
    // LogEntry.Source matches source. Replaces any prior handler for that
    // source (last one wins). Idempotent at startup time. Use when the
    // handler doesn't need the row's payload (e.g. opens a singleton detail
    // window).
    public void RegisterDetailHandler(string source, Action handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_detailHandlers) { _detailHandlers[source] = handler; }
    }

    // Register an entry-aware double-click handler. Used when the handler
    // needs the row's LogEntry.Context or LogEntry.Message — e.g. the
    // RoomClassifier unknown-entity fix dialog, which reads the raw "Also
    // Here:" line from Context and parses the unknown name out of Message.
    // Coexists with the no-arg variant; both fire on double-click.
    public void RegisterDetailHandler(string source, Action<LogEntry> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_entryHandlers) { _entryHandlers[source] = handler; }
    }

    // Invoke the no-arg detail handler for source, or return false if no
    // handler is registered.
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

    // Invoke the entry-aware detail handler for entry's source, or return
    // false if no entry-aware handler is registered. The LogPane fires both
    // this AND the no-arg overload on double-click so the two registration
    // styles can coexist.
    public bool TryInvokeDetailHandler(LogEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Source)) return false;
        Action<LogEntry>? handler;
        lock (_entryHandlers)
        {
            if (!_entryHandlers.TryGetValue(entry.Source, out handler)) return false;
        }
        handler(entry);
        return true;
    }

    // Maximum number of entries retained.
    public int Capacity { get; }

    // Live entries currently in the ring. Cheap (single integer read under
    // the gate) — readers that need the count shouldn't call Snapshot, which
    // allocates an entire ring-sized array per call.
    public int Count
    {
        get { lock (_gate) { return _count; } }
    }

    // Most recently appended entry, or null if nothing has been logged yet.
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

    // Append an entry. Stamps LogEntry.Timestamp at call time.
    public void Log(LogSeverity severity, string source, string message)
        => Log(severity, source, message, context: null);

    // Append an entry with an optional context payload — the raw wire line
    // (or producer-supplied string) that the LogPane's double-click handler
    // copies to the clipboard. See LogEntry.Context for the convention.
    public void Log(LogSeverity severity, string source, string message, string? context)
    {
        LogEntry entry = new(
            DateTimeOffset.Now,
            severity,
            source ?? string.Empty,
            message ?? string.Empty,
            context);

        lock (_gate)
        {
            _ring[_head] = entry;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
            _latest = entry;
        }

        EntryAdded?.Invoke(entry);
    }

    // Copy the live entries out in oldest-to-newest order. Returns a fresh
    // array; safe for the caller to retain.
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

    // Drop every entry. Does not fire EntryAdded.
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
    // Info / Warn / Error are always recorded. Debug / Combat are
    // generation-gated: they no-op unless the matching per-character
    // diagnostic toggle is on, so producers can call them unconditionally
    // and pay nothing (beyond the argument evaluation) when the channel is
    // off. Hot paths that build interpolated messages should still guard on
    // IsDebugEnabled / IsCombatEnabled to skip the string allocation.

    public void Info(string source, string message)  => Log(LogSeverity.Info,  source, message);
    public void Warn(string source, string message)  => Log(LogSeverity.Warn,  source, message);
    public void Error(string source, string message) => Log(LogSeverity.Error, source, message);

    public void Debug(string source, string message)
    {
        if (!IsDebugEnabled) return;
        Log(LogSeverity.Debug, source, message);
    }

    public void Combat(string source, string message)
    {
        if (!IsCombatEnabled) return;
        Log(LogSeverity.Combat, source, message);
    }

    // ----- Convenience shorthands with context payload ----------------
    // The Warn / Debug / Combat paths are the ones that need it today
    // (RoomClassifier emits Warn rows with the raw "Also Here" line carried
    // in Context; combat traces carry the raw wire line). Other severities
    // can grow their own overload when a producer wants one.

    // Append a Warn entry with a Context payload that the LogPane's
    // double-click handler copies to the clipboard. RoomClassifier uses this
    // for Unknown-entity rows.
    public void Warn(string source, string message, string? context)
        => Log(LogSeverity.Warn, source, message, context);

    // Append a Debug entry with a Context payload. Generation-gated on the
    // Debug toggle.
    public void Debug(string source, string message, string? context)
    {
        if (!IsDebugEnabled) return;
        Log(LogSeverity.Debug, source, message, context);
    }

    // Append a Combat entry with a Context payload. Generation-gated on the
    // Combat toggle.
    public void Combat(string source, string message, string? context)
    {
        if (!IsCombatEnabled) return;
        Log(LogSeverity.Combat, source, message, context);
    }
}
