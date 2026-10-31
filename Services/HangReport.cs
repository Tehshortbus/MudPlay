using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace MudPlay.Services;

// The Hang-<timestamp>.md PerformanceMonitor writes to the Desktop when the UI
// thread stops answering: the white window the OS offers to end, which throws
// nothing for CrashReporter to catch. Everything here runs off the UI thread
// (on the monitor's write lane) while the UI thread is stuck, so nothing in it
// touches a window or a control: the code location comes from what Measure
// recorded (names, files, lines), the rest from services that read under locks
// of their own.
//
// The point of the file is to name the code the UI thread was in: each scope
// open on it, outermost first, with the source file, member and line it was
// opened at, and how long it had been open. A reader goes straight there.
//
// The session, the sent commands and the program log are read on a thread of
// their own with a time limit (Gather), since the frozen thread may hold a lock
// one of them needs; a report without them still has the code location. They go
// through the bug report's scrub: no login, password or other player's corpse
// password reaches the file. When the scrub can't be built they are left out.
public static class HangReport
{
    public const string FilePrefix = "Hang-";
    public const int CommandsShown = 50;
    public const int LogEntriesShown = 200;
    private const string AfterwardsHeading = "## Afterwards";

    // Where the report reads the session from. Session: name and value pairs
    // (character, BBS, realm, the log file to send), read at the hang. Resources:
    // the process's figures beyond the managed heap, the windows' contents and the
    // session's activity (ProcessResources.ReportLines), read before the session,
    // since none of them waits on a lock the frozen thread may hold.
    public sealed record Sources(
        LogService Log,
        SentCommandTrail SentCommands,
        Func<BugReportScrub.Scrubber> Scrubber,
        Func<IReadOnlyList<(string Key, string Value)>> Session,
        Func<IReadOnlyList<(string Key, string Value)>>? Resources = null);

    // A scope open on the UI thread.
    public readonly record struct Frame(string Name, string File, string Member, int Line, DateTimeOffset Since, TimeSpan OpenFor);

    // A scope the UI thread closed, or a run of alike ones close together.
    public readonly record struct Activity(string Name, string File, string Member, int Line,
        DateTimeOffset FirstStartedAt, DateTimeOffset LastEndedAt, double LastMs, int Count, double MaxMs, double TotalMs);

    // What the monitor saw when the hang was called. Depth may be more than
    // Stack holds, when scopes nested deeper than are kept.
    public sealed record Snapshot(
        DateTimeOffset At,
        TimeSpan Unresponsive,
        IReadOnlyList<Frame> Stack,
        int Depth,
        IReadOnlyList<Activity> Recent,
        IReadOnlyList<string> OpenWindows,
        (string What, DateTimeOffset At)? LastWindowEvent,
        (string Name, DateTimeOffset At)? LastFocused,
        string? LongOperation = null,
        MemoryTrail? Memory = null,
        ThreadStacks.Capture? Stacks = null);

    // The process's memory at one moment. PrivateBytes is the last one read (it
    // is dear to read on Windows, so read less often), or null. With it, running
    // totals that tell a busy freeze from a waiting one: the CPU the process has
    // used, the time the GC has held every thread, and the lock waits; and the
    // thread pool's backlog then.
    public readonly record struct Memory(long Heap, long Allocated, long WorkingSet, long? PrivateBytes,
        long GcHeapSize, long GcCommitted, int Gen0, int Gen1, int Gen2,
        TimeSpan Cpu = default, TimeSpan GcPause = default, long LockContentions = 0,
        long PoolPending = 0, int PoolThreads = 0)
    {
        // Never GC.GetTotalMemory(true): that forces a collection. Touches no window.
        public static Memory Read(long? privateBytes)
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            return new Memory(GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes(), Environment.WorkingSet, privateBytes,
                info.HeapSizeBytes, info.TotalCommittedBytes, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                Environment.CpuUsage.TotalTime, GC.GetTotalPauseDuration(), System.Threading.Monitor.LockContentionCount,
                ThreadPool.PendingWorkItemCount, ThreadPool.ThreadCount);
        }
    }

    // Memory when the hang began and when the report was written, and a few
    // samples from the minutes before it.
    public sealed record MemoryTrail(DateTimeOffset StartAt, Memory Start, DateTimeOffset NowAt, Memory Now,
        IReadOnlyList<(DateTimeOffset At, Memory Memory)> Before)
    {
        public double AllocatedMbPerSecond =>
            (Now.Allocated - Start.Allocated) / Mib / Math.Max(0.001, (NowAt - StartAt).TotalSeconds);
    }

    // What was read from the Sources, not yet masked; Build masks it with
    // Scrubber. Missing says why the sections are absent, when they are.
    public sealed record Gathered(
        IReadOnlyList<(string Key, string Value)> Session,
        IReadOnlyList<string> Commands,
        string? Problems,
        IReadOnlyList<string> Log,
        BugReportScrub.Scrubber? Scrubber,
        string? Missing,
        IReadOnlyList<(string Key, string Value)>? Resources = null)
    {
        public static Gathered Nothing(string why) => new([], [], null, [], null, why);
    }

    // Reads the sources on a thread of its own, waiting at most timeout, and
    // takes whatever it had read by then. The program log is read from its ring,
    // which is never locked across a disk write; the session's names come last,
    // since one of them (the log file's name) waits on the program log's file
    // writer, which a stalled disk can hold. The log line saying a report is
    // being written is the reader's last act, for the same reason.
    public static Gathered Gather(Sources? sources, string fileName, TimeSpan unresponsive, TimeSpan timeout,
        string? memoryHeadline = null)
    {
        if (sources is null) return Gathered.Nothing("the client had not finished starting");
        Gathered? result = null;
        // Not disposed: a reader that outlives the wait still sets it.
        ManualResetEventSlim read = new();
        Thread reader = new(() =>
        {
            try { Read(sources, gathered => Volatile.Write(ref result, gathered)); }
            catch (Exception ex)
            {
                // Only the type: a message may quote a path or a name the scrub would have masked.
                if (Volatile.Read(ref result) is null)
                    Volatile.Write(ref result, Gathered.Nothing($"reading them failed ({ex.GetType().Name})"));
            }
            read.Set();
            sources.Log.Warn("Performance",
                $"The UI thread has not answered for {Seconds(unresponsive)}: writing {fileName} to the Desktop"
                + (memoryHeadline is null ? string.Empty : "; " + memoryHeadline));
        })
        { IsBackground = true, Name = "Hang report reader" };
        reader.Start();
        read.Wait(timeout);
        return Volatile.Read(ref result) ?? Gathered.Nothing(
            $"they did not answer within {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s: "
            + "the frozen UI thread may hold a lock one of them needs (the profile)");
    }

    // Publishes the logs and commands as soon as they are read, then again with
    // the session's names.
    private static void Read(Sources sources, Action<Gathered> publish)
    {
        BugReportScrub.Scrubber scrubber;
        try { scrubber = sources.Scrubber(); }
        catch (Exception ex)
        {
            publish(Gathered.Nothing(
                $"the saved account details could not be read ({ex.GetType().Name}), so they could not be masked"));
            return;
        }
        BugReportChatCensor censor = BugReportChatCensor.Default;
        List<string> commands = BugReportSentCommands.Build(sources.SentCommands, censor).Entries?
            .TakeLast(CommandsShown).ToList() ?? [];
        string problems = BugReportLogSections.Problems(sources.Log, censor).Body;
        List<string> log = BugReportLogSections.ProgramLog(sources.Log, censor).Entries?
            .TakeLast(LogEntriesShown).ToList() ?? [];
        Gathered logs = new([("Session", "(not read: its names did not answer in time)")], commands, problems, log, scrubber, null);
        publish(logs);
        if (sources.Resources is { } resources)
        {
            IReadOnlyList<(string Key, string Value)> read;
            // A figure that fails must not cost the report its session.
            try { read = resources(); }
            catch (Exception ex) { read = [("Process resources", $"(not read: {ex.GetType().Name})")]; }
            logs = logs with { Resources = read };
            publish(logs);
        }
        publish(logs with { Session = sources.Session() });
    }

    public static string Build(Snapshot snapshot, Gathered gathered)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(gathered);
        StringBuilder sb = new(capacity: 32 * 1024);
        sb.Append("# MudPlay hang report\n\n");
        sb.Append("_Captured ").Append(snapshot.At.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)).Append("_\n\n");

        sb.Append("## What to send\n\n")
          .Append("The client's window stopped answering: its UI thread, which draws the window and takes every click, ")
          .Append("was busy and never got back to it. Attach this file to a bug report on GitHub with a line on what ")
          .Append("you were doing, and the log file named below under *Log to send with this*. If the client came back ")
          .Append("on its own, the end of this file says when. Nothing in it is a password: the account details and ")
          .Append("other players' corpse passwords are masked as in a bug report.\n\n");

        sb.Append("## Hang\n\n");
        Kv(sb, "Version", AppInfo.Version);
        Kv(sb, "Build", Safe(() => ClientEnvironment.Build));
        Kv(sb, "OS", Safe(() => $"{ClientEnvironment.Os} ({ClientEnvironment.Architecture})"));
        Kv(sb, "Runtime", Safe(() => ClientEnvironment.Runtime));
        Kv(sb, "Desktop", Safe(() => ClientEnvironment.Desktop));
        Kv(sb, "Client started", Safe(() =>
            $"{ClientEnvironment.ProcessStart:yyyy-MM-dd HH:mm:ss}, process {ClientEnvironment.ProcessId}"));
        Kv(sb, "UI thread unresponsive", $"{Seconds(snapshot.Unresponsive)} when this was written "
            + $"(since {Time(snapshot.At - snapshot.Unresponsive)})");
        if (snapshot.LongOperation is { } longOperation)
            Kv(sb, "Inside a known long operation", longOperation + ". Such work is reported only past "
                + Seconds(PerformanceMonitor.LongHangAfter) + ", and this one ran past it");
        foreach ((string key, string value) in gathered.Session) Kv(sb, key, value);
        if (gathered.Missing is { } why)
            Kv(sb, "Session, commands and log", "not included: " + why);
        sb.Append('\n');

        AppendStack(sb, snapshot);
        AppendBusy(sb, snapshot);
        AppendMemory(sb, snapshot, gathered.Resources);
        AppendRecent(sb, snapshot);
        AppendWindows(sb, snapshot);
        AppendOtherStacks(sb, snapshot.Stacks);

        if (gathered.Missing is null)
        {
            sb.Append("## Commands sent (last ").Append(CommandsShown).Append(", oldest first)\n\n");
            AppendBlock(sb, gathered.Commands, "_(nothing sent while in the game this session)_");
            if (gathered.Problems is { } problems)
                sb.Append("## Warnings and errors (oldest first)\n\n").Append(problems.TrimEnd()).Append("\n\n");
            sb.Append("## Program log (last ").Append(LogEntriesShown).Append(" entries, oldest first)\n\n");
            AppendBlock(sb, gathered.Log, "_(log empty)_");
        }

        sb.Append(AfterwardsHeading).Append("\n\n");
        sb.Append("- ").Append(Time(snapshot.At)).Append(": written, the UI thread not answering for ")
          .Append(Seconds(snapshot.Unresponsive)).Append(".\n");

        string document = sb.ToString();
        return gathered.Scrubber is { } scrubber ? scrubber.Apply(document) : document;
    }

    private static void AppendStack(StringBuilder sb, Snapshot snapshot)
    {
        sb.Append("## Where the UI thread is\n\n");
        AppendUiCallStack(sb, snapshot.Stacks);
        if (snapshot.Stack.Count == 0)
        {
            sb.Append("No measured scope was open: the UI thread is in code no scope covers (layout, drawing, ")
              .Append("input, a window's own handlers). ")
              .Append(snapshot.Stacks?.Ui is { Frames.Count: > 0 }
                  ? "Its call stack above names the code.\n\n"
                  : "The activity and the windows below say what led up to it.\n\n");
            return;
        }
        sb.Append("The scopes open on the UI thread, outermost first. The last is the innermost measured code it ")
          .Append("was in; it may be inside a call that one makes.\n\n");
        for (int i = 0; i < snapshot.Stack.Count; i++)
        {
            Frame f = snapshot.Stack[i];
            sb.Append(i + 1).Append(". `").Append(f.Name).Append("` at `").Append(SourcePath(f.File)).Append(':')
              .Append(f.Line).Append("`, in `").Append(f.Member).Append("`: open ").Append(Seconds(f.OpenFor))
              .Append(" (since ").Append(Time(f.Since)).Append(")\n");
        }
        if (snapshot.Depth > snapshot.Stack.Count)
            sb.Append("\n_").Append(snapshot.Depth - snapshot.Stack.Count)
              .Append(" deeper scope(s) are not listed: only the outermost ").Append(snapshot.Stack.Count).Append(" are kept._\n");
        sb.Append('\n');
    }

    // The UI thread's call stack, read from a snapshot of the process (Windows).
    private static void AppendUiCallStack(StringBuilder sb, ThreadStacks.Capture? stacks)
    {
        if (stacks is null) return;
        if (stacks.Ui is not { Frames.Count: > 0 } ui)
        {
            sb.Append("_No call stack: ").Append(stacks.Missing ?? "the UI thread wasn't among the threads read").Append("._\n\n");
            return;
        }
        sb.Append("Its call stack when this was written, innermost first (read in ").Append(Ms(stacks.Took.TotalMilliseconds))
          .Append("). The first lines that name MudPlay code are where it is stuck; what is above them is what that ")
          .Append("code called, below it how it got there.\n\n```\n");
        AppendFrames(sb, ui);
        sb.Append("```\n\n");
    }

    private static void AppendFrames(StringBuilder sb, ThreadStacks.Stack stack)
    {
        foreach (string frame in stack.Frames) sb.Append(frame).Append('\n');
        if (stack.MoreFrames > 0) sb.Append("… ").Append(stack.MoreFrames).Append(" more frame(s)\n");
    }

    // The other threads' stacks: a lock the UI thread waits on is held by one of them.
    private static void AppendOtherStacks(StringBuilder sb, ThreadStacks.Capture? stacks)
    {
        if (stacks is null || stacks.Stacks.All(s => s.IsUi)) return;
        sb.Append("## Other threads\n\n")
          .Append("The client's other managed threads at the same moment, innermost frame first: a lock or a result ")
          .Append("the UI thread is waiting on is held or worked on by one of these.\n\n");
        foreach (ThreadStacks.Stack stack in stacks.Stacks.Where(s => !s.IsUi))
        {
            sb.Append("**Thread ").Append(stack.ManagedId).Append("** (OS ").Append(stack.OsId)
              .Append(stack.Role is { } role ? ", " + role : string.Empty).Append(")\n\n```\n");
            AppendFrames(sb, stack);
            sb.Append("```\n\n");
        }
    }

    // Busy, waiting or collecting: what the whole process did while the UI thread
    // was silent, from the figures read when the hang began and when this was written.
    private static void AppendBusy(StringBuilder sb, Snapshot snapshot)
    {
        if (snapshot.Memory is not { } m) return;
        double wall = (m.NowAt - m.StartAt).TotalSeconds;
        if (wall <= 0 || m.Now.Cpu == default) return;
        double cpu = (m.Now.Cpu - m.Start.Cpu).TotalSeconds;
        double gc = (m.Now.GcPause - m.Start.GcPause).TotalSeconds;
        sb.Append("## Busy, waiting or collecting\n\n")
          .Append("What the whole client did between the hang beginning (").Append(Time(m.StartAt)).Append(") and this ")
          .Append("being written (").Append(Time(m.NowAt)).Append("). About one core's worth of CPU means a thread was ")
          .Append("computing; next to none means the UI thread was waiting (on a lock, the disk, another thread). Time the ")
          .Append("GC held every thread is time no code ran at all.\n\n");
        Kv(sb, "CPU used", $"{cpu.ToString("0.0", CultureInfo.InvariantCulture)} s in {wall.ToString("0.0", CultureInfo.InvariantCulture)} s, "
            + $"about {(cpu / wall).ToString("0.0", CultureInfo.InvariantCulture)} cores' worth");
        Kv(sb, "GC held every thread", $"{gc.ToString("0.00", CultureInfo.InvariantCulture)} s "
            + $"({(gc / wall * 100).ToString("0", CultureInfo.InvariantCulture)}% of it), "
            + $"collections gen0 / gen1 / gen2 +{m.Now.Gen0 - m.Start.Gen0} / +{m.Now.Gen1 - m.Start.Gen1} / +{m.Now.Gen2 - m.Start.Gen2}");
        Kv(sb, "Waits for a lock another thread held", $"{m.Now.LockContentions - m.Start.LockContentions}");
        Kv(sb, "Thread pool", $"{m.Now.PoolThreads} thread(s), {m.Now.PoolPending} work item(s) queued");
        sb.Append('\n');
    }

    private static void AppendRecent(StringBuilder sb, Snapshot snapshot)
    {
        sb.Append("## Last UI-thread activity (oldest first)\n\n");
        if (snapshot.Recent.Count == 0)
        {
            sb.Append("_(no measured scope finished on the UI thread this session)_\n\n");
            return;
        }
        sb.Append("The last measured work the UI thread finished: when it ended, how long it took, what it was and ")
          .Append("where. Work repeated close together is one row, with its count.\n\n```\n");
        foreach (Activity a in snapshot.Recent)
        {
            sb.Append(Time(a.LastEndedAt)).Append("  ").Append(Ms(a.LastMs).PadLeft(9)).Append("  ").Append(a.Name)
              .Append("  ").Append(SourcePath(a.File)).Append(':').Append(a.Line).Append(" (").Append(a.Member).Append(')');
            if (a.Count > 1)
                sb.Append("  x").Append(a.Count).Append(" since ").Append(Time(a.FirstStartedAt))
                  .Append(", max ").Append(Ms(a.MaxMs)).Append(", ").Append(Ms(a.TotalMs)).Append(" in all");
            sb.Append('\n');
        }
        sb.Append("```\n\n");
    }

    private static void AppendWindows(StringBuilder sb, Snapshot snapshot)
    {
        sb.Append("## Windows\n\n");
        Kv(sb, "Open", snapshot.OpenWindows.Count == 0 ? "(none)" : string.Join(", ", snapshot.OpenWindows));
        Kv(sb, "Last opened or closed", snapshot.LastWindowEvent is { } e ? $"{e.What} at {Time(e.At)}" : "(none)");
        Kv(sb, "Last focused", snapshot.LastFocused is { } f ? $"{f.Name} at {Time(f.At)}" : "(none)");
        sb.Append('\n');
    }

    // A line for the Afterwards section while the hang goes on.
    public static string StillLine(DateTimeOffset at, TimeSpan waited, Frame? innermost, Memory start, Memory now) =>
        $"- {Time(at)}: still not answering after {Seconds(waited)}; innermost scope "
        + (innermost is { } f
            ? $"`{f.Name}` at `{SourcePath(f.File)}:{f.Line}`, in `{f.Member}`, open {Seconds(f.OpenFor)}"
            : "none")
        + $"; {MemoryLine(start, now)}.\n";

    public static string InterruptedLine(DateTimeOffset at, TimeSpan stopped) =>
        $"- {Time(at)}: the freeze watch was itself stopped for {Seconds(stopped)} (the machine slept, or the "
        + "process was paused). The figures above end there; a freeze that went on after it is counted anew.\n";

    public static string RecoveredLine(DateTimeOffset at, TimeSpan lasted, Memory start, Memory now) =>
        $"- {Time(at)}: the UI thread answered again. The hang lasted {Seconds(lasted)} ({MemoryLine(start, now)}).\n";

    // What memory did since the hang began, in a few words.
    public static string MemoryLine(Memory start, Memory now) =>
        $"{SignedSize(now.Allocated - start.Allocated)} allocated, heap {Size(now.Heap)}, working set {Size(now.WorkingSet)}";

    // The program log's line: the headline numbers.
    public static string MemoryHeadline(MemoryTrail trail) =>
        $"working set {Size(trail.Now.WorkingSet)} ({SignedSize(trail.Now.WorkingSet - trail.Start.WorkingSet)} since it stopped), "
        + $"heap {Size(trail.Now.Heap)}, {Rate(trail.AllocatedMbPerSecond)} allocated";

    private static void AppendMemory(StringBuilder sb, Snapshot snapshot, IReadOnlyList<(string Key, string Value)>? resources)
    {
        if (snapshot.Memory is not { } m)
        {
            if (resources is not { Count: > 0 }) return;
            sb.Append("## Memory\n\n");
            foreach ((string key, string value) in resources) Kv(sb, key, value);
            sb.Append('\n');
            return;
        }
        sb.Append("## Memory\n\n")
          .Append("When the hang began (").Append(Time(m.StartAt)).Append(") and when this was written (")
          .Append(Time(m.NowAt)).Append("). A client that runs away grows here: its working set climbs into the ")
          .Append("gigabytes, with one core busy, while a sound one sits at a few hundred MB. Working set is what sits in ")
          .Append("RAM, private bytes all the client has asked for: memory taken but not yet written shows only in the second.\n\n");
        Kv(sb, "Working set", Change(m.Start.WorkingSet, m.Now.WorkingSet));
        Kv(sb, "Private bytes", m.Start.PrivateBytes is { } p0 && m.Now.PrivateBytes is { } p1 ? Change(p0, p1)
            : m.Now.PrivateBytes is { } only ? Size(only) : "(not read)");
        Kv(sb, "Managed heap", Change(m.Start.Heap, m.Now.Heap));
        Kv(sb, "GC heap size / committed", $"{Size(m.Now.GcHeapSize)} / {Size(m.Now.GcCommitted)}");
        Kv(sb, "Allocated since the hang began", $"{Size(m.Now.Allocated - m.Start.Allocated)}, "
            + $"{Rate(m.AllocatedMbPerSecond)} over {Seconds(m.NowAt - m.StartAt)}");
        Kv(sb, "Collections gen0 / gen1 / gen2", $"{m.Start.Gen0} / {m.Start.Gen1} / {m.Start.Gen2} then, "
            + $"{m.Now.Gen0} / {m.Now.Gen1} / {m.Now.Gen2} now");
        if (resources is not null)
            foreach ((string key, string value) in resources) Kv(sb, key, value);
        if (m.Before.Count > 0)
        {
            sb.Append("\nBefore the hang (oldest first; the rate is since the row above, or since the first row):\n\n```\n");
            for (int i = 0; i < m.Before.Count; i++)
            {
                (DateTimeOffset at, Memory mem) = m.Before[i];
                (DateTimeOffset prevAt, Memory prev) = i == 0 ? m.Before[0] : m.Before[i - 1];
                double seconds = (at - prevAt).TotalSeconds;
                sb.Append(Time(at)).Append("  working set ").Append(Size(mem.WorkingSet).PadLeft(8))
                  .Append("  heap ").Append(Size(mem.Heap).PadLeft(8));
                if (seconds > 0)
                    sb.Append("  ").Append(Rate((mem.Allocated - prev.Allocated) / Mib / seconds)).Append(" allocated");
                sb.Append('\n');
            }
            sb.Append("```\n");
        }
        sb.Append('\n');
    }

    private const double Mib = 1024.0 * 1024.0;

    private static string Size(long bytes) =>
        Math.Abs(bytes) >= 1024 * Mib
            ? (bytes / (1024 * Mib)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : (bytes / Mib).ToString("0", CultureInfo.InvariantCulture) + " MB";

    public static string SizeOf(long bytes) => Size(bytes);

    private static string SignedSize(long bytes) => (bytes >= 0 ? "+" : "-") + Size(Math.Abs(bytes));

    private static string Change(long then, long now) => $"{Size(then)} then, {Size(now)} now ({SignedSize(now - then)})";

    private static string Rate(double mbPerSecond) => mbPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";

    private static void AppendBlock(StringBuilder sb, IReadOnlyList<string> lines, string empty)
    {
        if (lines.Count == 0)
        {
            sb.Append(empty).Append("\n\n");
            return;
        }
        sb.Append("```\n");
        foreach (string line in lines) sb.Append(line).Append('\n');
        sb.Append("```\n\n");
    }

    // A source file as the repository has it (Services/ProfileService.cs), from
    // the build machine's full path the compiler recorded.
    public static string SourcePath(string file)
    {
        if (string.IsNullOrEmpty(file)) return "(unknown file)";
        string root = SourceRoot;
        string path = root.Length > 0 && file.StartsWith(root, StringComparison.Ordinal) ? file[root.Length..] : file;
        return path.Replace('\\', '/');
    }

    // The repository root on the machine that built this: where this file was,
    // less its own place in the tree.
    private static readonly string SourceRoot = RootOf(ThisFile());

    private static string ThisFile([CallerFilePath] string file = "") => file;

    private static string RootOf(string thisFile)
    {
        foreach (string suffix in new[] { "Services/HangReport.cs", "Services\\HangReport.cs" })
            if (thisFile.EndsWith(suffix, StringComparison.Ordinal)) return thisFile[..^suffix.Length];
        return string.Empty;
    }

    public static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string Ms(double ms) => ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms";

    private static string Time(DateTimeOffset at) => at.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) { return $"(unknown: {ex.GetType().Name})"; }
    }

    private static void Kv(StringBuilder sb, string key, string value) =>
        sb.Append("- **").Append(key).Append("**: ").Append(value).Append('\n');
}
