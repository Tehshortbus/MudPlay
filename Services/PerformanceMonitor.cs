using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
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
// Stalls are found by a ping: a probe thread posts a no-op at the lowest
// dispatcher priority, which runs only once the UI thread has caught up on
// everything ahead of it. The wait before it runs is the stall. While a ping is
// overdue the probe samples what the UI thread said it was doing (Measure), and a
// window that opened or closed during the stall is named too.
//
// The probe runs for the whole session, whatever Auto-collect logs says: once a
// second with collecting off, which costs one post a second. A ping still
// unanswered after HangAfter is a freeze, the kind that leaves the window white
// until the user kills the client, with no exception for CrashReporter to catch.
// The probe then has Hang-<ts>.md written to the Desktop (HangReport), off the
// UI thread and off its own, from what Measure left behind: the scopes open on
// the UI thread, each with the file and member it was opened in, and the ones
// that ran just before. Inside a known long operation (MeasureLong) the
// threshold is LongHangAfter instead.
//
// Kept out of the program log for the same reason as MemoryUsageLog: a line per
// hitch would bury the entries an operator reads. With collecting off nothing is
// timed or written to the performance log.
public sealed class PerformanceMonitor : IAsyncDisposable
{
    public const double StallMs = 50;
    private static readonly TimeSpan PingInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(1);
    private const int SamplesPerTiming = 1024;

    public static readonly TimeSpan HangAfter = TimeSpan.FromSeconds(5);
    // The threshold while a known long operation is open (MeasureLong).
    public static readonly TimeSpan LongHangAfter = TimeSpan.FromSeconds(30);
    // A probe tick this much later than it was due means the probe itself was
    // stopped (the machine slept, a debugger paused the process): the ping's age
    // then says nothing about the UI thread, and is counted again from here.
    private static readonly TimeSpan ProbeLate = TimeSpan.FromSeconds(3);
    // A hang still going at each of these gets a line in its report, with the
    // innermost scope at that moment: a scope that changes is a loop, not a wait.
    private static readonly TimeSpan[] StillHungAt =
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
    // One report per freeze, and freezes in a row don't carpet the Desktop.
    public static readonly TimeSpan ReportGap = TimeSpan.FromMinutes(2);
    public const int MaxHangReports = 5;
    // How long the report waits for the logs and the session's names, read on a
    // thread of their own: the frozen UI thread may hold a lock one of them takes.
    private static readonly TimeSpan GatherTimeout = TimeSpan.FromSeconds(2);
    // A process snapshot is read in well under a second; a Desktop left waiting
    // longer than this for one is worse than a report without stacks.
    private static readonly TimeSpan StacksTimeout = TimeSpan.FromSeconds(10);

    // The UI thread's open scopes kept, and the finished ones remembered.
    private const int MaxDepth = 32;
    private const int ActivityCapacity = 50;
    // A scope that finished like one of the last few (a render each frame, a feed
    // each packet) adds to that row instead of pushing older rows out.
    private const int CollapseWindow = 8;

    private const string HeaderLine =
        "# 'stall' lines: the UI thread fell behind by the time shown, while doing what follows. " +
        "'summary' lines, every minute: stalls, then each kind of work as count/p50/p95/max ms, " +
        "then CPU, memory and GC over that minute, then the types most allocated (sampled).";

    private readonly LogDiagnosticState? _diagnostics;
    private readonly Func<LogFileHeader.Session>? _session;
    private readonly Func<bool> _onUiThread;
    private readonly Func<long> _timestamp;
    private readonly ProbeHost? _probe;
    // Made once, not per tick and per ping.
    private readonly Action _tick, _onPing;
    private volatile bool _disposed;

    // Where lines go: the open log file, or a test's sink. Null while not collecting.
    private readonly object _gate = new();
    private DebugLogWriter? _writer;
    private Action<string>? _sink;
    private bool _broken;
    private AllocationSampler? _allocations;
    private volatile bool _collecting;

    // What the UI thread is doing: the scopes Measure opened on it and hasn't
    // closed, outermost first, the last ones it closed, and what each known long
    // operation took (last, max, ms). Written on the UI thread, read by the probe;
    // the lock is never held for more than a copy.
    private readonly object _scopesLock = new();
    private readonly Frame[] _stack = new Frame[MaxDepth];
    private int _depth;
    private readonly Ran[] _ran = new Ran[ActivityCapacity];
    private int _ranHead, _ranCount;
    private readonly Dictionary<string, (double Last, double Max)> _longRuns = new(StringComparer.Ordinal);

    // The windows open, by type, and the last opened, closed or focused.
    private readonly object _windowLock = new();
    private readonly List<string> _openWindows = new();
    private (string What, long At)? _lastWindowEvent;
    private (string Name, long At)? _lastFocused;

    // Timestamp of the ping in flight; 0 when none is. Read by both threads.
    private long _pingPostedAt;
    private long _pingAnsweredAt;
    // The probe's own state, touched only on the probe's thread.
    private long _lastPingAt, _lastTickAt;
    private readonly HashSet<string> _seenDuringPing = new();
    private readonly object _pingLock = new();
    private Hang? _hang;
    private int _reportsWritten;
    private long _lastReportAt;
    // Nothing before this (a timestamp) counts toward a hang: the client's own
    // start, before its window is up, may keep the UI thread for seconds.
    private long _armedAt;
    // The UI thread's managed id, taken on it as the main window opens: whose call
    // stack a hang report leads with.
    private int _uiThreadId;
    // When the last known long operation closed on the UI thread (a timestamp), 0
    // before any has.
    private long _lastLongEnd;

    // Memory, sampled on the probe's tick once a second (about 20 us: the GC's own
    // counters and the working set), into a fixed ring the probe alone touches.
    // Private bytes cost more to read, so they are read every PrivateBytesEvery,
    // and every PrivateBytesInHang while a hang is open.
    private const int MemoryRingSize = 180;
    private static readonly TimeSpan MemoryEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PrivateBytesEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrivateBytesInHang = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] MemoryBeforeAt =
        [TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)];
    private readonly (long At, HangReport.Memory Memory)[] _memory = new (long, HangReport.Memory)[MemoryRingSize];
    private int _memoryHead, _memoryCount;
    private long _privateBytesAt;
    private long? _privateBytes;
    // The bug report's line, one entry per freeze. The write lane's alone.
    private readonly List<string> _freezeEntries = new();

    private volatile HangReport.Sources? _hangSources;
    // For a bug report: the freezes seen this session. The count is the probe's;
    // the note is written on the write lane.
    private int _freezesSeen;
    private volatile string _freezesNote = "none";
    private readonly AsideSlot _logSlot = new(), _summarySlot = new();

    // The minute being summed up, reset by each summary.
    private readonly object _statsLock = new();
    private readonly Dictionary<string, Timing> _timings = new(StringComparer.Ordinal);
    private int _stalls, _stalls250, _stalls1000;
    private double _stalledMs, _worstStallMs;
    private Baseline _since = Baseline.Now();

    // The production monitor: follows Auto-collect logs, probes the Avalonia
    // dispatcher from its own thread, and names windows as they open, close and
    // take focus. session: whose session the performance log's header names.
    public PerformanceMonitor(LogDiagnosticState diagnostics, Func<LogFileHeader.Session>? session = null)
        : this(() => Dispatcher.UIThread.CheckAccess(), ProbeHost.Client(), diagnostics, session)
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>(
            (window, _) => NoteWindowOpened(window.GetType().Name), RoutingStrategies.Direct);
        Window.WindowClosedEvent.AddClassHandler<Window>(
            (window, _) => NoteWindowClosed(window.GetType().Name), RoutingStrategies.Direct);
        Window.IsActiveProperty.Changed.AddClassHandler<Window>((window, e) =>
        {
            if (e.GetNewValue<bool>()) NoteWindowFocused(window.GetType().Name);
        });
    }

    // probe: what the probe runs on, or null for a monitor with no probe, whose
    // stalls and timings are fed in directly.
    internal PerformanceMonitor(Func<bool> onUiThread, ProbeHost? probe = null, LogDiagnosticState? diagnostics = null,
        Func<LogFileHeader.Session>? session = null)
    {
        _onUiThread = onUiThread;
        _session = session;
        _probe = probe;
        _timestamp = probe?.Timestamp ?? Stopwatch.GetTimestamp;
        _diagnostics = diagnostics;
        if (_diagnostics is not null)
        {
            _diagnostics.Changed += SyncWriter;
            SyncWriter();
        }
        _tick = ProbeTick;
        _onPing = OnPing;
        _probe?.Schedule(IdleInterval, _tick);
    }

    // What the probe runs on. In the client: a thread of its own, pinging the
    // Avalonia dispatcher, writing to the Desktop. WriteAside runs the report's
    // file work in order off the probe's thread; RunAside runs work that may
    // block (a log line, the performance log's summary) on a thread of its own.
    internal sealed record ProbeHost(
        Action<Action> PingUi,
        Action<TimeSpan, Action> Schedule,
        Func<long> Timestamp,
        Func<DateTimeOffset> Now,
        Func<string> ReportFolder,
        Action<Action> WriteAside,
        Action<Action> RunAside,
        Func<long?, HangReport.Memory> ReadMemory,
        Func<long?> ReadPrivateBytes,
        Func<int, ThreadStacks.Capture>? ReadStacks = null)
    {
        public static ProbeHost Client()
        {
            ProbeThread thread = new();
            WriteLane lane = new();
            // Kept for the life of the process: read again on each Refresh.
            Process self = Process.GetCurrentProcess();
            return new ProbeHost(
                a => Dispatcher.UIThread.Post(a, DispatcherPriority.Background),
                thread.Schedule,
                Stopwatch.GetTimestamp,
                () => DateTimeOffset.Now,
                CrashReporter.ReportFolder,
                lane.Add,
                RunOnOwnThread,
                HangReport.Memory.Read,
                () => PrivateBytesOf(self),
                uiThread => ThreadStacks.Read(uiThread, StacksTimeout));
        }

        private static void RunOnOwnThread(Action work) =>
            new Thread(() => work()) { IsBackground = true, Name = "UI probe aside" }.Start();

        // About 130 us on Linux; on Windows Process.Refresh can take milliseconds,
        // which is why it is read every PrivateBytesEvery, not every tick.
        private static long? PrivateBytesOf(Process self)
        {
            try
            {
                self.Refresh();
                return self.PrivateMemorySize64;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
            {
                return null;   // no process figures on this platform: the report says "(not read)"
            }
        }
    }

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

    // Where a hang report reads the session, the sent commands and the program
    // log from, and the scrub it passes them through. Until set, a report holds
    // only what the monitor itself saw.
    public HangReport.Sources? HangSources
    {
        get => _hangSources;
        set => _hangSources = value;
    }

    // The freezes seen this session and the last one's file, for a bug report.
    public string FreezesThisSession => _freezesNote;

    // From now on a UI thread silent for HangAfter is reported. Called on the UI
    // thread once the main window has opened.
    public void ArmHangReports()
    {
        if (Interlocked.CompareExchange(ref _armedAt, Math.Max(1, _timestamp()), 0) != 0) return;
        Volatile.Write(ref _uiThreadId, Environment.CurrentManagedThreadId);
        _hangSources?.Log.Info("Performance",
            $"Freeze watch armed: a UI thread silent {HangReport.Seconds(HangAfter)} or more "
            + $"({HangReport.Seconds(LongHangAfter)} inside a known long operation) is written to a Hang file on the Desktop");
    }

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
                    _writer = new DebugLogWriter("performance", LogFileHeader.Read(_session));
                    _writer.WriteLine(HeaderLine);
                    _sink = WriteToFile;
                    _collecting = true;
                    _allocations = new AllocationSampler();
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
        }
    }

    private void Write(string line)
    {
        lock (_gate) _sink?.Invoke(line);
    }

    // ----- Measure ---------------------------------------------------------

    // Times a piece of work under name. On the UI thread the scope is also put on
    // the stack a hang report prints, with the file, member and line it was opened
    // at (filled in by the compiler), and once closed among the last activities.
    // Nested scopes stack. Off the UI thread it is free while not collecting.
    // detail tells one run from another (a section's id, the file a save writes);
    // it is kept apart from name so nothing is put together until it is read, and
    // a full path is shown by its file name.
    public Scope Measure(string name, string? detail = null,
        [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0) =>
        Open(name, detail, expectedLong: false, quiet: false, file, member, line);

    // As Measure, for work that holds the UI thread for seconds by design: a
    // game-data set loaded, switched or re-imported. Client policy (2026-10-18):
    // while one is open on the UI thread a freeze is reported only past
    // LongHangAfter, since such a load is routine and a Hang file for each would
    // bury the real freezes. One that runs past it is still reported, and the
    // report names it and what it has taken before.
    public Scope MeasureLong(string name, string? detail = null,
        [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0) =>
        Open(name, detail, expectedLong: true, quiet: false, file, member, line);

    // quiet: left out of the recent activity when it ran under QuietBelowMs (Raise).
    private Scope Open(string name, string? detail, bool expectedLong, bool quiet, string file, string member, int line)
    {
        bool timed = _collecting;
        if (!_onUiThread()) return timed ? new Scope(this, name, detail, -1, _timestamp(), timed: true) : default;
        long start = _timestamp();
        int depth;
        lock (_scopesLock)
        {
            depth = _depth++;
            if (depth < MaxDepth) _stack[depth] = new Frame(name, detail, file, member, line, start, expectedLong, quiet);
        }
        return new Scope(this, name, detail, depth, start, timed);
    }

    // ----- Raise -----------------------------------------------------------

    // Raises an event one handler at a time, each in a scope of its own named for
    // the handler: a hang report then names the slow subscriber, where a scope
    // round the raise could name only the line of game text that raised it. A
    // handler that runs StallMs or more is said in the log, at Debug, in category.
    // A room change has some fifty handlers, so one that finishes under
    // QuietBelowMs stays out of the recent activity: fifty rows a step would push
    // out everything else the report's list is for.
    public void Raise(Action? handlers, string what, LogService? log = null, string category = "Performance",
        [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
            RaiseOne(handler, static (h, _) => ((Action)h)(), (object?)null, what, log, category, file, member, line);
    }

    public void Raise<T>(Action<T>? handlers, T arg, string what, LogService? log = null, string category = "Performance",
        [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
            RaiseOne(handler, static (h, a) => ((Action<T>)h)(a), arg, what, log, category, file, member, line);
    }

    // Under this, a raised handler's scope is left out of the recent activity.
    private const double QuietBelowMs = 1;

    private void RaiseOne<T>(Delegate handler, Action<Delegate, T> invoke, T arg, string what, LogService? log,
        string category, string file, string member, int line)
    {
        string name = HandlerName(handler);
        long started = _timestamp();
        using (Open(what, name, expectedLong: false, quiet: true, file, member, line)) invoke(handler, arg);
        double ms = Ms(started, _timestamp());
        if (log is not null && ms >= StallMs)
            log.Debug(category, $"{what} handler '{name}' took {ms:F0} ms");
    }

    // A handler subscribed as a lambda has no name of its own (the compiler's reads
    // "<.ctor>b__1644_1001"), so one that is raised through Raise subscribes
    // through this, to be named by what it does. Each call makes a new delegate: to
    // unsubscribe, keep the one subscribed and `-=` that; `-=` with a fresh
    // Named(...) removes nothing.
    public static Action<T> Named<T>(string name, Action<T> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        return new NamedHandler<T>(name, handler).Invoke;
    }

    public static Action Named(string name, Action handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        return new NamedAction(name, handler).Invoke;
    }

    private interface INamedHandler
    {
        string Name { get; }
    }

    private sealed class NamedHandler<T>(string name, Action<T> handler) : INamedHandler
    {
        public string Name { get; } = name;
        public void Invoke(T arg) => handler(arg);
    }

    private sealed class NamedAction(string name, Action handler) : INamedHandler
    {
        public string Name { get; } = name;
        public void Invoke() => handler();
    }

    private static readonly ConditionalWeakTable<Delegate, string> HandlerNames = new();

    private static string HandlerName(Delegate handler) =>
        HandlerNames.GetValue(handler, static d => d.Target is INamedHandler named
            ? named.Name
            : $"{d.Method.DeclaringType?.Name ?? "?"}.{d.Method.Name}");

    public readonly struct Scope : IDisposable
    {
        private readonly PerformanceMonitor? _monitor;
        private readonly string _name;
        private readonly string? _detail;
        private readonly int _depth;
        private readonly long _start;
        private readonly bool _timed;

        internal Scope(PerformanceMonitor monitor, string name, string? detail, int depth, long start, bool timed)
        {
            _monitor = monitor;
            _name = name;
            _detail = detail;
            _depth = depth;
            _start = start;
            _timed = timed;
        }

        public void Dispose() => _monitor?.Close(_name, _detail, _depth, _start, _timed);
    }

    private void Close(string name, string? detail, int depth, long start, bool timed)
    {
        long end = _timestamp();
        double ms = Ms(start, end);
        if (timed && _collecting) Record(Describe(name, detail), ms);
        if (depth < 0) return;
        lock (_scopesLock)
        {
            if (depth < MaxDepth)
            {
                ref Frame f = ref _stack[depth];
                if (!f.Quiet || ms >= QuietBelowMs) NoteRan(in f, end, ms);
                if (f.ExpectedLong)
                {
                    _longRuns[f.Name] = (ms, Math.Max(_longRuns.GetValueOrDefault(f.Name).Max, ms));
                    Interlocked.Exchange(ref _lastLongEnd, end);
                }
                f = default;
            }
            // Back to the depth this scope was opened at, so a scope left open by
            // a throw that skipped its Dispose can't keep the stack deep.
            _depth = depth;
        }
    }

    // A scope's name with its detail, as the report and the summary show it.
    private static string Describe(string name, string? detail) =>
        detail is null ? name : name + " " + (Path.IsPathRooted(detail) ? Path.GetFileName(detail) : detail);

    // Under _scopesLock.
    private void NoteRan(in Frame f, long end, double ms)
    {
        for (int i = 0; i < Math.Min(_ranCount, CollapseWindow); i++)
        {
            ref Ran r = ref _ran[(_ranHead - 1 - i + ActivityCapacity) % ActivityCapacity];
            if (r.Line != f.Line || !string.Equals(r.Name, f.Name, StringComparison.Ordinal)
                || !string.Equals(r.Detail, f.Detail, StringComparison.Ordinal)
                || !string.Equals(r.File, f.File, StringComparison.Ordinal))
                continue;
            r.Count++;
            r.LastEnd = end;
            r.LastMs = ms;
            r.TotalMs += ms;
            r.MaxMs = Math.Max(r.MaxMs, ms);
            return;
        }
        _ran[_ranHead] = new Ran
        {
            Name = f.Name, Detail = f.Detail, File = f.File, Member = f.Member, Line = f.Line,
            FirstStart = f.Start, LastEnd = end, LastMs = ms, TotalMs = ms, MaxMs = ms, Count = 1,
        };
        _ranHead = (_ranHead + 1) % ActivityCapacity;
        if (_ranCount < ActivityCapacity) _ranCount++;
    }

    private string? InnermostScope()
    {
        lock (_scopesLock)
        {
            if (_depth == 0) return null;
            ref Frame f = ref _stack[Math.Min(_depth, MaxDepth) - 1];
            return Describe(f.Name, f.Detail);
        }
    }

    // The innermost known long operation open on the UI thread, if any. Any
    // depth counts: a set switch reads its tables in scopes of their own.
    private (string Name, long Start)? OpenLongOperation()
    {
        lock (_scopesLock)
        {
            for (int i = Math.Min(_depth, MaxDepth) - 1; i >= 0; i--)
                if (_stack[i].ExpectedLong) return (Describe(_stack[i].Name, _stack[i].Detail), _stack[i].Start);
            return null;
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

    // ----- Windows -----------------------------------------------------------

    // The window class handlers call these on the UI thread with the window's
    // type name: the probe reads names, never a window. Each open and close goes
    // in the program log with the windows then open, so a log read on its own
    // says what was on screen when memory or the UI went wrong. Logged whatever
    // Auto-collect logs says, as the client's status line is: a window opens
    // rarely, and the line costs no more than that.
    internal void NoteWindowOpened(string name)
    {
        string open;
        lock (_windowLock)
        {
            _openWindows.Add(name);
            _lastWindowEvent = ($"opening {name}", _timestamp());
            open = DescribeWindows(_openWindows);
        }
        _hangSources?.Log.Info(WindowsLogSource, $"opened {name} (open: {open})");
    }

    internal void NoteWindowClosed(string name)
    {
        string open;
        lock (_windowLock)
        {
            _openWindows.Remove(name);
            _lastWindowEvent = ($"closing {name}", _timestamp());
            open = DescribeWindows(_openWindows);
        }
        _hangSources?.Log.Info(WindowsLogSource, $"closed {name} (open: {open})");
    }

    public const string WindowsLogSource = "Windows";

    // The windows open now, short: "Main, Conversation, InfoDialog x2". Any thread.
    public string OpenWindowsSummary
    {
        get { lock (_windowLock) return DescribeWindows(_openWindows); }
    }

    // Under _windowLock. Type names lose their "Window" ending; repeats are counted.
    private static string DescribeWindows(List<string> open)
    {
        if (open.Count == 0) return "none";
        const string suffix = "Window";
        return string.Join(", ", open.GroupBy(n => n, StringComparer.Ordinal).Select(g =>
        {
            string shortName = g.Key.Length > suffix.Length && g.Key.EndsWith(suffix, StringComparison.Ordinal)
                ? g.Key[..^suffix.Length] : g.Key;
            int count = g.Count();
            return count == 1 ? shortName : $"{shortName} x{count}";
        }));
    }

    // The GC handle count the runtime last reported, heard while collecting (the
    // runtime gives it no API of its own); null otherwise.
    public long? GcHandleCount => _allocations?.GcHandles;

    internal void NoteWindowFocused(string name)
    {
        lock (_windowLock) _lastFocused = (name, _timestamp());
    }

    // ----- The probe ---------------------------------------------------------
    //
    // The probe thread never touches a disk. A stalled disk (an antivirus scan, a
    // file Windows holds) is one of the things a freeze can be, and a probe that
    // waited on it would go silent with the UI. So the Hang file and its later
    // lines go to the write lane (ProbeHost.WriteAside), one after another, and
    // the log lines and the performance log's summary each to a thread of their
    // own (ProbeHost.RunAside), at most one of each kind at a time: the program
    // log's file writer may be what the UI thread is stuck in.

    private void ProbeTick()
    {
        if (_disposed || _probe is not { } probe) return;
        try
        {
            Probe(probe);
        }
        catch (Exception ex)
        {
            // A diagnostic must not take the client down, and on this thread any
            // throw would: this tick is lost, the next one runs as usual.
            LogAside(probe, LogSeverity.Warn, $"UI probe tick failed: {ex.GetType().Name}: {ex.Message}");
        }
        probe.Schedule(_collecting ? WatchInterval : IdleInterval, _tick);
    }

    private void Probe(ProbeHost probe)
    {
        long now = _timestamp();
        TimeSpan due = _collecting ? WatchInterval : IdleInterval;
        if (_lastTickAt != 0 && Elapsed(_lastTickAt, now) - due > ProbeLate)
        {
            long inFlight = Interlocked.Read(ref _pingPostedAt);
            if (inFlight != 0) Interlocked.CompareExchange(ref _pingPostedAt, now, inFlight);
            if (_hang is { } interrupted)
            {
                Interrupted(probe, interrupted, Elapsed(_lastTickAt, now));
                _hang = null;
            }
        }
        _lastTickAt = now;
        SampleMemory(probe, now);

        long posted = Interlocked.Read(ref _pingPostedAt);
        if (posted == 0)
        {
            if (_hang is { } ended)
            {
                Recovered(probe, ended, Interlocked.Read(ref _pingAnsweredAt));
                _hang = null;
            }
            if (Elapsed(_lastPingAt, now) >= (_collecting ? PingInterval : IdleInterval) - TimeSpan.FromMilliseconds(1))
            {
                _lastPingAt = now;
                Interlocked.Exchange(ref _pingPostedAt, now);
                probe.PingUi(_onPing);
            }
        }
        else
        {
            TimeSpan waited = Elapsed(posted, now);
            if (_collecting && waited.TotalMilliseconds >= StallMs && InnermostScope() is { } doing)
                lock (_pingLock) _seenDuringPing.Add(doing);
            long armed = Interlocked.Read(ref _armedAt);
            // A hang is counted from the latest of: the ping going out, the arm, and
            // the end of the last known long operation. A ping that went out before
            // the arm (the start still running when the window opened) then still
            // catches a freeze that began there, without putting the start in its
            // figures; and the work queued behind a long set switch (its handlers,
            // layout, drawing) gets the usual HangAfter from the switch's end, not
            // from a ping that waited through the whole switch.
            long from = Math.Max(posted, Math.Max(armed, Interlocked.Read(ref _lastLongEnd)));
            if (_hang is { } hang) StillHung(probe, hang, now);
            else if (armed != 0 && Elapsed(from, now) is var counted && counted >= HangAfter
                     && !Debugger.IsAttached
                     && (counted >= LongHangAfter || OpenLongOperation() is null))
                _hang = Hung(probe, from, now);
        }

        if (_collecting)
        {
            bool summaryDue;
            lock (_statsLock) summaryDue = DateTime.UtcNow - _since.AtUtc >= SummaryInterval;
            if (summaryDue)
            {
                string summary = Summary();
                _summarySlot.Run(probe, dropped => () => Write(dropped == 0 ? summary
                    : $"{summary} | {dropped} summary line(s) before this one dropped: the disk did not answer"));
            }
        }
    }

    // A line for the program log, off the probe's thread.
    private void LogAside(ProbeHost probe, LogSeverity severity, string line)
    {
        if (_hangSources?.Log is not { } log) return;
        _logSlot.Run(probe, dropped => () => log.Log(severity, "Performance", dropped == 0 ? line
            : $"{line} ({dropped} earlier line(s) from the freeze watch dropped: the log did not answer)"));
    }

    // On the UI thread: it has caught up.
    private void OnPing()
    {
        long answered = _timestamp();
        Interlocked.Exchange(ref _pingAnsweredAt, answered);
        long posted = Interlocked.Exchange(ref _pingPostedAt, 0);
        if (posted == 0 || !_collecting) return;
        double ms = Ms(posted, answered);
        List<string> causes;
        lock (_pingLock)
        {
            causes = _seenDuringPing.ToList();
            _seenDuringPing.Clear();
        }
        lock (_windowLock)
        {
            if (_lastWindowEvent is { } window && window.At >= posted)
                causes.Add(window.What);
        }
        if (ms >= StallMs) RecordStall(ms, causes.Count == 0 ? "unattributed" : string.Join(", ", causes));
    }

    // A hang being reported: when it is counted from, and, filled in on the
    // write lane, the file it went to or why none was written. The probe never
    // reads Path or NoFile; it only hands the lane work.
    private sealed class Hang(long countedFrom, int number, HangReport.Memory start, string? noFile = null)
    {
        public long CountedFrom { get; } = countedFrom;
        public int Number { get; } = number;
        public HangReport.Memory StartMemory { get; } = start;
        // The probe's: the highest working set sampled while it lasts.
        public long PeakWorkingSet { get; set; } = start.WorkingSet;
        public string? Path { get; set; }
        public string? NoFile { get; set; } = noFile;
        public int NextMilestone { get; set; }
    }

    private Hang Hung(ProbeHost probe, long from, long now)
    {
        int number = ++_freezesSeen;
        DateTimeOffset at = probe.Now();
        string? why = _reportsWritten >= MaxHangReports ? $"the session's {MaxHangReports} are written"
            : _lastReportAt != 0 && Elapsed(_lastReportAt, now) < ReportGap
                ? $"one was written less than {ReportGap.TotalMinutes:0} minutes before"
                : null;
        HangReport.Memory start = MemoryAt(from)?.Memory ?? LatestMemory(probe);
        Hang hang = new(from, number, start, why);
        hang.PeakWorkingSet = Math.Max(start.WorkingSet, LatestMemory(probe).WorkingSet);
        long peak = hang.PeakWorkingSet;
        if (why is not null)
        {
            probe.WriteAside(() => NoteFreeze(hang, $"at {at:HH:mm:ss}, still going", peak));
            return hang;
        }

        _reportsWritten++;
        _lastReportAt = now;
        HangReport.Snapshot snapshot = TakeSnapshot(probe, from, now, at, start);
        HangReport.Sources? sources = _hangSources;
        probe.WriteAside(() => WriteReport(probe, hang, snapshot, sources, peak));
        return hang;
    }

    // On the write lane. A Desktop that is missing or can't be written is given
    // up quietly, as CrashReporter does: the bug report's freeze line and the
    // recovery log line say why.
    private void WriteReport(ProbeHost probe, Hang hang, HangReport.Snapshot snapshot, HangReport.Sources? sources, long peak)
    {
        string name = $"{HangReport.FilePrefix}{snapshot.At:yyyyMMdd-HHmmss}.md";
        // First, while the UI thread is still where it froze.
        if (probe.ReadStacks is { } readStacks)
            snapshot = snapshot with { Stacks = readStacks(Volatile.Read(ref _uiThreadId)) };
        HangReport.Gathered gathered = HangReport.Gather(sources, name, snapshot.Unresponsive, GatherTimeout,
            snapshot.Memory is { } memory ? HangReport.MemoryHeadline(memory) : null);
        try
        {
            string path = Path.Combine(probe.ReportFolder(), name);
            File.WriteAllText(path, HangReport.Build(snapshot, gathered));
            hang.Path = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hang.NoFile = $"the Desktop could not be written ({ex.GetType().Name})";
        }
        NoteFreeze(hang, $"at {snapshot.At:HH:mm:ss}, still going", peak);
    }

    // On the write lane: this freeze's entry in the bug report's line, the last
    // few freezes shown.
    private void NoteFreeze(Hang hang, string how, long peakWorkingSet)
    {
        string entry = $"#{hang.Number} {how}, peak working set {HangReport.SizeOf(peakWorkingSet)}"
            + (hang.Path is { } path ? $", {Path.GetFileName(path)}" : $", no Hang file: {hang.NoFile}");
        while (_freezeEntries.Count < hang.Number) _freezeEntries.Add(string.Empty);
        _freezeEntries[hang.Number - 1] = entry;
        const int shown = 5;
        _freezesNote = $"{_freezeEntries.Count}: " + string.Join("; ", _freezeEntries.TakeLast(shown))
            + (_freezeEntries.Count > shown ? $" (and {_freezeEntries.Count - shown} earlier)" : string.Empty);
    }

    private void StillHung(ProbeHost probe, Hang hang, long now)
    {
        if (hang.NextMilestone >= StillHungAt.Length) return;
        TimeSpan waited = Elapsed(hang.CountedFrom, now);
        if (waited < StillHungAt[hang.NextMilestone]) return;
        while (hang.NextMilestone < StillHungAt.Length && waited >= StillHungAt[hang.NextMilestone]) hang.NextMilestone++;
        DateTimeOffset at = probe.Now();
        HangReport.Frame? innermost;
        lock (_scopesLock)
            innermost = _depth > 0 ? ToFrame(in _stack[Math.Min(_depth, MaxDepth) - 1], now, at) : null;
        string line = HangReport.StillLine(at, waited, innermost, hang.StartMemory, LatestMemory(probe));
        probe.WriteAside(() => Append(hang, line));
    }

    private void Recovered(ProbeHost probe, Hang hang, long answeredAt)
    {
        TimeSpan lasted = answeredAt > hang.CountedFrom ? Elapsed(hang.CountedFrom, answeredAt) : HangAfter;
        DateTimeOffset at = probe.Now();
        HangReport.Memory now = LatestMemory(probe);
        long peak = hang.PeakWorkingSet;
        probe.WriteAside(() =>
        {
            Append(hang, HangReport.RecoveredLine(at, lasted, hang.StartMemory, now));
            NoteFreeze(hang, $"ended {at:HH:mm:ss} after {HangReport.Seconds(lasted)}", peak);
            LogAside(probe, LogSeverity.Warn, $"The UI thread answered again after {HangReport.Seconds(lasted)} unresponsive"
                + (hang.Path is { } path ? $"; see {Path.GetFileName(path)}" : $" (no Hang file: {hang.NoFile})"));
        });
    }

    // The probe itself was stopped mid-hang (a sleep, a paused process): what it
    // would count from here on is that pause, not the UI thread, so the hang's
    // figures stop where they were. A UI thread still silent afterwards is a new
    // hang, counted from the probe's return.
    private void Interrupted(ProbeHost probe, Hang hang, TimeSpan stopped)
    {
        DateTimeOffset at = probe.Now();
        long peak = hang.PeakWorkingSet;
        probe.WriteAside(() =>
        {
            Append(hang, HangReport.InterruptedLine(at, stopped));
            NoteFreeze(hang, $"cut short at {at:HH:mm:ss} by a {HangReport.Seconds(stopped)} sleep or pause", peak);
            LogAside(probe, LogSeverity.Info, $"The freeze watch was itself stopped for {HangReport.Seconds(stopped)} "
                + "(the machine slept, or the process was paused): the freeze it was timing ends there");
        });
    }

    // On the write lane: a line added to a report that was written.
    private static void Append(Hang hang, string text)
    {
        if (hang.Path is not { } path) return;
        try { File.AppendAllText(path, text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The report stands as first written; the log line on recovery says how long it lasted.
        }
    }

    private HangReport.Snapshot TakeSnapshot(ProbeHost probe, long from, long now, DateTimeOffset wall, HangReport.Memory start)
    {
        // Read fresh, private bytes with it: once, for the report.
        HangReport.Memory current = probe.ReadMemory(probe.ReadPrivateBytes());
        List<(DateTimeOffset, HangReport.Memory)> before = new();
        foreach (TimeSpan back in MemoryBeforeAt)
        {
            long at = from - (long)(back.TotalSeconds * Stopwatch.Frequency);
            if (MemoryAt(at) is { } sample && sample.At < from && (before.Count == 0 || Wall(sample.At, now, wall) > before[^1].Item1))
                before.Add((Wall(sample.At, now, wall), sample.Memory));
        }
        HangReport.MemoryTrail memory = new(Wall(MemoryAt(from)?.At ?? now, now, wall), start, wall, current, before);

        List<HangReport.Frame> frames = new();
        List<HangReport.Activity> recent = new();
        int depth;
        string? longOperation = null;
        lock (_scopesLock)
        {
            depth = _depth;
            for (int i = 0; i < Math.Min(depth, MaxDepth); i++) frames.Add(ToFrame(in _stack[i], now, wall));
            for (int i = 0; i < _ranCount; i++)
            {
                ref Ran r = ref _ran[(_ranHead - _ranCount + i + ActivityCapacity) % ActivityCapacity];
                recent.Add(new HangReport.Activity(Describe(r.Name, r.Detail), r.File, r.Member, r.Line,
                    Wall(r.FirstStart, now, wall), Wall(r.LastEnd, now, wall), r.LastMs, r.Count, r.MaxMs, r.TotalMs));
            }
            for (int i = Math.Min(depth, MaxDepth) - 1; i >= 0 && longOperation is null; i--)
            {
                ref Frame f = ref _stack[i];
                if (!f.ExpectedLong) continue;
                longOperation = $"`{Describe(f.Name, f.Detail)}`, open {HangReport.Seconds(Elapsed(f.Start, now))}: "
                    + (_longRuns.TryGetValue(f.Name, out (double Last, double Max) runs)
                        ? $"this session it has taken up to {HangReport.Seconds(TimeSpan.FromMilliseconds(runs.Max))}, "
                          + $"{HangReport.Seconds(TimeSpan.FromMilliseconds(runs.Last))} the last time"
                        : "it had not finished before this session");
            }
        }
        recent.Sort((a, b) => a.LastEndedAt.CompareTo(b.LastEndedAt));
        string[] open;
        (string What, DateTimeOffset At)? lastEvent = null;
        (string Name, DateTimeOffset At)? lastFocused = null;
        lock (_windowLock)
        {
            open = _openWindows.ToArray();
            if (_lastWindowEvent is { } e) lastEvent = (e.What, Wall(e.At, now, wall));
            if (_lastFocused is { } f) lastFocused = (f.Name, Wall(f.At, now, wall));
        }
        return new HangReport.Snapshot(wall, Elapsed(from, now), frames, depth, recent, open, lastEvent, lastFocused,
            longOperation, memory);
    }

    private HangReport.Frame ToFrame(in Frame f, long now, DateTimeOffset wall) =>
        new(Describe(f.Name, f.Detail), f.File, f.Member, f.Line, Wall(f.Start, now, wall), Elapsed(f.Start, now));

    private static DateTimeOffset Wall(long at, long now, DateTimeOffset wallNow) => wallNow - Elapsed(at, now);

    private static TimeSpan Elapsed(long from, long to) =>
        TimeSpan.FromSeconds((double)(to - from) / Stopwatch.Frequency);

    private static double Ms(long from, long to) => (double)(to - from) * 1000 / Stopwatch.Frequency;

    // On the probe's tick: once a second, the memory figures into the ring.
    private void SampleMemory(ProbeHost probe, long now)
    {
        if (_memoryCount > 0 && Elapsed(_memory[(_memoryHead - 1 + MemoryRingSize) % MemoryRingSize].At, now)
                < MemoryEvery - TimeSpan.FromMilliseconds(1))
            return;
        if (_privateBytesAt == 0
            || Elapsed(_privateBytesAt, now) >= (_hang is null ? PrivateBytesEvery : PrivateBytesInHang) - TimeSpan.FromMilliseconds(1))
        {
            _privateBytes = probe.ReadPrivateBytes();
            _privateBytesAt = now;
        }
        HangReport.Memory memory = probe.ReadMemory(_privateBytes);
        _memory[_memoryHead] = (now, memory);
        _memoryHead = (_memoryHead + 1) % MemoryRingSize;
        if (_memoryCount < MemoryRingSize) _memoryCount++;
        if (_hang is { } hang) hang.PeakWorkingSet = Math.Max(hang.PeakWorkingSet, memory.WorkingSet);
    }

    private HangReport.Memory LatestMemory(ProbeHost probe) =>
        _memoryCount > 0 ? _memory[(_memoryHead - 1 + MemoryRingSize) % MemoryRingSize].Memory : probe.ReadMemory(_privateBytes);

    // The last sample taken at or before at, else the oldest kept; null with none.
    private (long At, HangReport.Memory Memory)? MemoryAt(long at)
    {
        if (_memoryCount == 0) return null;
        (long At, HangReport.Memory Memory)? found = null;
        for (int i = 0; i < _memoryCount; i++)
        {
            (long At, HangReport.Memory Memory) sample = _memory[(_memoryHead - _memoryCount + i + MemoryRingSize) % MemoryRingSize];
            if (sample.At > at) return found ?? sample;
            found = sample;
        }
        return found;
    }

    // Work handed off the probe's thread that may block, one of a kind at a time:
    // while one is still out, the next is counted and dropped, so a disk or a log
    // that never answers can't pile threads up behind it.
    private sealed class AsideSlot
    {
        private int _busy;
        private int _dropped;

        // make: given how many were dropped since the last one went out, the work.
        public void Run(ProbeHost probe, Func<int, Action> make)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            Action work = make(Interlocked.Exchange(ref _dropped, 0));
            probe.RunAside(() =>
            {
                try { work(); }
                finally { Volatile.Write(ref _busy, 0); }
            });
        }
    }

    // ----- Stalls and the minute's summary -----------------------------------

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
        _disposed = true;
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

    // A scope open on the UI thread.
    private readonly record struct Frame(string Name, string? Detail, string File, string Member, int Line, long Start,
        bool ExpectedLong, bool Quiet);

    // A scope the UI thread closed, or a run of alike ones.
    private struct Ran
    {
        public string Name, File, Member;
        public string? Detail;
        public int Line, Count;
        public long FirstStart, LastEnd;
        public double LastMs, TotalMs, MaxMs;
    }

    // The client's probe: one background thread that sleeps out each delay and
    // runs the tick, which schedules the next. Its own thread rather than a timer,
    // so a starved thread pool can't silence it.
    private sealed class ProbeThread
    {
        private readonly object _gate = new();
        private (TimeSpan Delay, Action Tick)? _next;
        private Thread? _thread;

        public void Schedule(TimeSpan delay, Action tick)
        {
            lock (_gate)
            {
                _next = (delay, tick);
                if (_thread is null)
                {
                    _thread = new Thread(Run)
                    {
                        IsBackground = true, Name = "UI responsiveness probe", Priority = ThreadPriority.AboveNormal,
                    };
                    _thread.Start();
                }
                else Monitor.Pulse(_gate);
            }
        }

        private void Run()
        {
            while (true)
            {
                (TimeSpan Delay, Action Tick) next;
                lock (_gate)
                {
                    while (_next is null) Monitor.Wait(_gate);
                    next = _next.Value;
                    _next = null;
                }
                Thread.Sleep(next.Delay);
                next.Tick();
            }
        }
    }

    // The report's file work, in order, on one background thread. A write stuck
    // on the disk holds back only the work after it; past MaxQueued waiting, the
    // newest is dropped, since a lane that stuck will not catch up.
    private sealed class WriteLane
    {
        private const int MaxQueued = 64;
        private readonly object _gate = new();
        private readonly Queue<Action> _work = new();
        private Thread? _thread;

        public void Add(Action work)
        {
            lock (_gate)
            {
                if (_work.Count >= MaxQueued) return;
                _work.Enqueue(work);
                if (_thread is null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "Hang report writer" };
                    _thread.Start();
                }
                else Monitor.Pulse(_gate);
            }
        }

        private void Run()
        {
            while (true)
            {
                Action next;
                lock (_gate)
                {
                    while (_work.Count == 0) Monitor.Wait(_gate);
                    next = _work.Dequeue();
                }
                try { next(); }
                catch (Exception)
                {
                    // A report that fails in a way not foreseen is given up quietly:
                    // the client goes on, and the next report still has its chance.
                }
            }
        }
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
        private long _gcHandles = -1;

        // From the GC's heap statistics, raised at the end of every collection.
        public long? GcHandles => Interlocked.Read(ref _gcHandles) is var n && n >= 0 ? n : null;

        protected override void OnEventSourceCreated(EventSource source)
        {
            // GC keyword at Verbose: the level the allocation tick is raised at.
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName is null || e.PayloadNames is not { } names || e.Payload is not { } payload) return;
            if (e.EventName.StartsWith("GCHeapStats", StringComparison.Ordinal))
            {
                int at = names.IndexOf("GCHandleCount");
                if (at >= 0 && at < payload.Count)
                    Interlocked.Exchange(ref _gcHandles, Convert.ToInt64(payload[at], CultureInfo.InvariantCulture));
                return;
            }
            if (!e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal)) return;
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
