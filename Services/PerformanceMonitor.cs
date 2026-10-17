using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MudPlay.Services;

// How smoothly the UI thread runs, measured in the user's own sessions: every
// stutter (a burst of output, a window open, a save) is a stretch of time the UI
// thread spent on something other than painting and input. While Auto-collect
// logs is on, each one is written to its own Logs/{ts}-performance.log as it
// happens, with what was running, and a summary line every minute says what the
// regular work cost, so a session's file shows when it lagged and why.
//
// Stalls are found by a ping: every PingInterval a watcher thread posts a no-op at
// the lowest dispatcher priority, which runs only once the UI thread has caught up
// on everything ahead of it. The wait before it runs is the stall. While a ping is
// overdue the watcher samples what the UI thread said it was doing (Measure), and
// a window that opened or closed during the stall is named too.
//
// Kept out of the program log for the same reason as MemoryUsageLog: a line per
// hitch would bury the entries an operator reads. With collecting off (the
// default) nothing is pinged, timed or written, and the watcher thread doesn't exist.
public sealed class PerformanceMonitor : IAsyncDisposable
{
    public const double StallMs = 50;
    private static readonly TimeSpan PingInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(1);
    private const int SamplesPerTiming = 1024;

    private const string HeaderLine =
        "# 'stall' lines: the UI thread fell behind by the time shown, while doing what follows. " +
        "'summary' lines, every minute: stalls, then each kind of work as count/p50/p95/max ms, " +
        "then CPU, memory and GC over that minute, then the types most allocated (sampled).";

    private readonly LogDiagnosticState? _diagnostics;
    private readonly Action<Action>? _postLowPriority;
    private readonly Func<bool> _onUiThread;
    // The watcher thread only exists while the log file is open. Each start takes a
    // new run number; a thread leaves its loop once it's no longer the current run
    // (collecting was turned off, or off and on again).
    private int _watcherRun;

    // Where lines go: the open log file, or a test's sink. Null while not collecting.
    private readonly object _gate = new();
    private DebugLogWriter? _writer;
    private Action<string>? _sink;
    private bool _broken;
    private AllocationSampler? _allocations;
    private volatile bool _collecting;

    // What the UI thread is doing, set by Measure on the UI thread and read by the
    // watcher. A plain reference write is atomic; volatile keeps it fresh.
    private volatile string? _activity;

    // Stopwatch timestamp of the ping in flight; 0 when none is.
    private long _pingPostedAt;
    private long _lastPingAt;
    private readonly HashSet<string> _seenDuringPing = new();
    private (string What, long At)? _lastWindowEvent;
    private readonly object _pingLock = new();

    // The minute being summed up, reset by each summary.
    private readonly object _statsLock = new();
    private readonly Dictionary<string, Timing> _timings = new(StringComparer.Ordinal);
    private int _stalls, _stalls250, _stalls1000;
    private double _stalledMs, _worstStallMs;
    private Baseline _since = Baseline.Now();

    // The production monitor: follows Auto-collect logs, pings the Avalonia
    // dispatcher from its own thread, and names windows as they open.
    public PerformanceMonitor(LogDiagnosticState diagnostics)
        : this(a => Dispatcher.UIThread.Post(a, DispatcherPriority.Background),
               () => Dispatcher.UIThread.CheckAccess())
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _diagnostics.Changed += SyncWriter;
        Window.WindowOpenedEvent.AddClassHandler<Window>(
            (window, _) => NoteWindowEvent($"opening {window.GetType().Name}"), RoutingStrategies.Direct);
        Window.WindowClosedEvent.AddClassHandler<Window>(
            (window, _) => NoteWindowEvent($"closing {window.GetType().Name}"), RoutingStrategies.Direct);
        SyncWriter();
    }

    // A monitor with no watcher thread or file behind it; stalls and timings are fed in directly.
    internal PerformanceMonitor(Action<Action>? postLowPriority, Func<bool> onUiThread)
    {
        _postLowPriority = postLowPriority;
        _onUiThread = onUiThread;
    }

    private void StartWatcher()
    {
        int run = Interlocked.Increment(ref _watcherRun);
        new Thread(() => Watch(run)) { IsBackground = true, Name = "UI stall probe", Priority = ThreadPriority.AboveNormal }
            .Start();
    }

    private void StopWatcher() => Interlocked.Increment(ref _watcherRun);

    // Collects into sink instead of a file.
    internal void CollectTo(Action<string>? sink)
    {
        lock (_gate)
        {
            _sink = sink;
            _collecting = sink is not null;
        }
        ResetMinute();
    }

    // True while the log file is open.
    public bool IsCollecting => _collecting;

    // Open or close the file to match Auto-collect logs. Once a write fails, stay
    // closed for the session, as the other diagnostic files do.
    private void SyncWriter()
    {
        bool want = _diagnostics?.AutoCollectLogs ?? false;
        DebugLogWriter? closing = null;
        lock (_gate)
        {
            if (_broken) return;
            if (want && _writer is null)
            {
                try
                {
                    _writer = new DebugLogWriter("performance");
                    _writer.WriteLine(HeaderLine);
                    _sink = WriteToFile;
                    _collecting = true;
                    _allocations = new AllocationSampler();
                    StartWatcher();
                }
                catch (IOException) { _broken = true; }
                catch (UnauthorizedAccessException) { _broken = true; }
            }
            else if (!want && _writer is not null)
            {
                closing = _writer;
                _writer = null;
                _sink = null;
                _collecting = false;
                _allocations?.Dispose();
                _allocations = null;
                StopWatcher();
            }
        }
        closing?.Dispose();
        ResetMinute();
    }

    private void WriteToFile(string line)
    {
        try
        {
            _writer?.WriteLine(line);
        }
        catch (IOException)
        {
            // Disk full or the file went away: stop, as MemoryUsageLog does. Losing
            // the trail is acceptable; failing the UI thread over it is not.
            _broken = true;
            _writer?.Dispose();
            _writer = null;
            _sink = null;
            _collecting = false;
            StopWatcher();
        }
    }

    private void Write(string line)
    {
        lock (_gate) _sink?.Invoke(line);
    }

    // Times a piece of work under name. On the UI thread it is also what a stall
    // seen meanwhile is put down to. Nested scopes restore the outer name. Free
    // while not collecting.
    public Scope Measure(string name)
    {
        if (!_collecting) return default;
        bool onUi = _onUiThread();
        string? outer = null;
        if (onUi)
        {
            outer = _activity;
            _activity = name;
        }
        return new Scope(this, name, onUi, outer, Stopwatch.GetTimestamp());
    }

    public readonly struct Scope : IDisposable
    {
        private readonly PerformanceMonitor? _monitor;
        private readonly string _name;
        private readonly bool _onUi;
        private readonly string? _outer;
        private readonly long _start;

        internal Scope(PerformanceMonitor monitor, string name, bool onUi, string? outer, long start)
        {
            _monitor = monitor;
            _name = name;
            _onUi = onUi;
            _outer = outer;
            _start = start;
        }

        public void Dispose()
        {
            if (_monitor is null) return;
            _monitor.Record(_name, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
            if (_onUi) _monitor._activity = _outer;
        }
    }

    internal void Record(string name, double ms)
    {
        lock (_statsLock)
        {
            if (!_timings.TryGetValue(name, out Timing? timing))
            {
                timing = new Timing();
                _timings[name] = timing;
            }
            timing.Add(ms);
        }
    }

    private void NoteWindowEvent(string what)
    {
        if (!_collecting) return;
        lock (_pingLock) _lastWindowEvent = (what, Stopwatch.GetTimestamp());
    }

    private void Watch(int run)
    {
        while (Volatile.Read(ref _watcherRun) == run)
        {
            Thread.Sleep(WatchInterval);
            long now = Stopwatch.GetTimestamp();
            long posted = Interlocked.Read(ref _pingPostedAt);
            if (posted == 0)
            {
                if (Stopwatch.GetElapsedTime(_lastPingAt, now) >= PingInterval)
                {
                    _lastPingAt = now;
                    Interlocked.Exchange(ref _pingPostedAt, now);
                    _postLowPriority!(OnPing);
                }
            }
            else if (Stopwatch.GetElapsedTime(posted, now).TotalMilliseconds >= StallMs
                     && _activity is { } doing)
            {
                lock (_pingLock) _seenDuringPing.Add(doing);
            }

            bool due;
            lock (_statsLock) due = DateTime.UtcNow - _since.AtUtc >= SummaryInterval;
            if (due) Write(Summary());
        }
    }

    private void OnPing()
    {
        long posted = Interlocked.Exchange(ref _pingPostedAt, 0);
        if (posted == 0) return;
        double ms = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
        List<string> causes;
        lock (_pingLock)
        {
            causes = _seenDuringPing.ToList();
            _seenDuringPing.Clear();
            if (_lastWindowEvent is { } window && window.At >= posted)
                causes.Add(window.What);
        }
        if (ms >= StallMs) RecordStall(ms, causes.Count == 0 ? "unattributed" : string.Join(", ", causes));
    }

    internal void RecordStall(double ms, string cause)
    {
        lock (_statsLock)
        {
            _stalls++;
            if (ms >= 250) _stalls250++;
            if (ms >= 1000) _stalls1000++;
            _stalledMs += ms;
            _worstStallMs = Math.Max(_worstStallMs, ms);
        }
        Write($"stall {ms.ToString("0", CultureInfo.InvariantCulture)} ms  {cause}");
    }

    // One line for the minute just ended, which then starts the next.
    internal string Summary()
    {
        Baseline now = Baseline.Now();
        StringBuilder sb = new("summary");
        lock (_statsLock)
        {
            sb.Append(" | stalls ").Append(_stalls);
            if (_stalls > 0)
                sb.Append(" (").Append(_stalls250).Append(" over 250 ms, ").Append(_stalls1000).Append(" over 1 s)")
                  .Append(", worst ").Append(F0(_worstStallMs)).Append(" ms, ").Append(F0(_stalledMs)).Append(" ms in all");
            foreach ((string name, Timing t) in _timings.OrderByDescending(kv => kv.Value.TotalMs))
            {
                (double p50, double p95) = t.Percentiles();
                sb.Append(" | ").Append(name).Append(' ').Append(t.Count).Append('/')
                  .Append(F1(p50)).Append('/').Append(F1(p95)).Append('/').Append(F1(t.MaxMs));
            }
            sb.Append(now.Since(_since));
            if (_allocations?.TakeTop(8) is { Length: > 0 } top) sb.Append(" | allocated ").Append(top);
            _timings.Clear();
            _stalls = _stalls250 = _stalls1000 = 0;
            _stalledMs = _worstStallMs = 0;
            _since = now;
        }
        return sb.ToString();
    }

    private void ResetMinute()
    {
        lock (_statsLock)
        {
            _timings.Clear();
            _stalls = _stalls250 = _stalls1000 = 0;
            _stalledMs = _worstStallMs = 0;
            _since = Baseline.Now();
        }
    }

    private static string F0(double v) => v.ToString("0", CultureInfo.InvariantCulture);
    private static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        StopWatcher();
        _allocations?.Dispose();
        if (_diagnostics is not null) _diagnostics.Changed -= SyncWriter;
        DebugLogWriter? writer;
        lock (_gate)
        {
            writer = _writer;
            _writer = null;
            _sink = null;
            _collecting = false;
        }
        if (writer is not null) await writer.DisposeAsync();
    }

    // What the program allocates most, by type, from the runtime's own sampling: an
    // allocation-tick event roughly every 100 KB allocated, naming the type of the
    // object that crossed the line. A steady 30 MB/s of garbage is what drives the
    // collections that show up as unattributed stalls; the types point at the code
    // making it. Listens only while collecting.
    private sealed class AllocationSampler : EventListener
    {
        // Initialised before EventListener's constructor runs, which already calls
        // OnEventSourceCreated for the sources that exist.
        private readonly Dictionary<string, long> _bytesByType = new(StringComparer.Ordinal);
        private long _total;

        protected override void OnEventSourceCreated(EventSource source)
        {
            // GC keyword at Verbose: the level the allocation tick is raised at.
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal)
                || e.PayloadNames is not { } names || e.Payload is not { } payload)
                return;
            string type = "?";
            long bytes = 0;
            for (int i = 0; i < names.Count; i++)
            {
                switch (names[i])
                {
                    case "TypeName": type = payload[i] as string ?? "?"; break;
                    case "AllocationAmount64": bytes = Convert.ToInt64(payload[i], CultureInfo.InvariantCulture); break;
                    case "AllocationAmount" when bytes == 0: bytes = Convert.ToInt64(payload[i], CultureInfo.InvariantCulture); break;
                }
            }
            lock (_bytesByType)
            {
                _bytesByType[type] = _bytesByType.GetValueOrDefault(type) + bytes;
                _total += bytes;
            }
        }

        // The top types since the last call, as shares of what was sampled.
        public string TakeTop(int count)
        {
            lock (_bytesByType)
            {
                if (_total == 0) return string.Empty;
                long total = _total;
                string top = string.Join(", ", _bytesByType.OrderByDescending(kv => kv.Value).Take(count)
                    .Select(kv => $"{kv.Key} {kv.Value * 100.0 / total:0}%"));
                _bytesByType.Clear();
                _total = 0;
                return top;
            }
        }
    }

    // Process-wide counters at a moment, so a summary can report the minute's
    // change: CPU time, GC collections and pause, bytes allocated.
    private readonly record struct Baseline(DateTime AtUtc, TimeSpan Cpu, int Gen0, int Gen1, int Gen2,
        TimeSpan Pause, long Allocated)
    {
        public static Baseline Now()
        {
            using Process process = Process.GetCurrentProcess();
            return new Baseline(DateTime.UtcNow, process.TotalProcessorTime, GC.CollectionCount(0),
                GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration(), GC.GetTotalAllocatedBytes());
        }

        public string Since(Baseline then)
        {
            double seconds = Math.Max(0.001, (AtUtc - then.AtUtc).TotalSeconds);
            using Process process = Process.GetCurrentProcess();
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            return " | cpu " + F1((Cpu - then.Cpu).TotalSeconds / seconds * 100) + "% of a core"
                + " | ws " + Mb(process.WorkingSet64) + " heap " + Mb(gc.HeapSizeBytes) + " committed " + Mb(gc.TotalCommittedBytes)
                + " | gc " + (Gen0 - then.Gen0) + "/" + (Gen1 - then.Gen1) + "/" + (Gen2 - then.Gen2)
                + " pause " + F0((Pause - then.Pause).TotalMilliseconds) + " ms"
                + " | alloc " + F1((Allocated - then.Allocated) / (1024.0 * 1024.0) / seconds) + " MB/s";
        }

        private static string Mb(long bytes) => F0(bytes / (1024.0 * 1024.0)) + "MB";
    }

    // A running count, total and maximum, plus the latest SamplesPerTiming times
    // for the percentiles.
    private sealed class Timing
    {
        private readonly double[] _samples = new double[SamplesPerTiming];
        private int _next;
        public int Count { get; private set; }
        public double TotalMs { get; private set; }
        public double MaxMs { get; private set; }

        public void Add(double ms)
        {
            _samples[_next] = ms;
            _next = (_next + 1) % _samples.Length;
            Count++;
            TotalMs += ms;
            MaxMs = Math.Max(MaxMs, ms);
        }

        public (double P50, double P95) Percentiles()
        {
            int n = Math.Min(Count, _samples.Length);
            if (n == 0) return (0, 0);
            double[] sorted = _samples[..n];
            Array.Sort(sorted);
            return (sorted[(int)Math.Round(0.50 * (n - 1))], sorted[(int)Math.Round(0.95 * (n - 1))]);
        }
    }
}
