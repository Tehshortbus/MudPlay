using System.Collections.Concurrent;
using System.Text;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The always-written last minutes of the program log: bounded by time and size,
// written off the thread that logs, absent while Auto-collect logs is on, and
// cleaned up across client processes.
public sealed class RecentLogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-recent-log-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public RecentLogFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathA => Path.Combine(_dir, "2026-10-10_12-00-00-p1-recent-a.log");
    private string PathB => Path.Combine(_dir, "2026-10-10_12-00-00-p1-recent-b.log");

    private static LogEntry Entry(DateTimeOffset at, string message) =>
        new(at, LogSeverity.Info, "Walker", message);

    private RecentLogFile Make(LogDiagnosticState? diagnostics = null, Func<string, TextWriter>? open = null) =>
        new(new LogService(), diagnostics ?? new LogDiagnosticState(), PathA, PathB, () => ["# header"], open);

    [Fact]
    public async Task KeepsBetweenOneAndTwoSpansOfTheNewestEntries()
    {
        await using RecentLogFile file = Make();

        // An entry every 10 s for 9 minutes: 4.5 spans.
        for (int i = 0; i < 54; i++) file.Enqueue(Entry(T0.AddSeconds(i * 10), $"step {i}"));
        await file.WhenWrittenAsync();

        string all = File.ReadAllText(PathA) + File.ReadAllText(PathB);
        // The newest entry is there; the first minutes are gone.
        Assert.Contains("step 53", all);
        Assert.DoesNotContain("step 0\n", all);
        int kept = Enumerable.Range(0, 54).Count(i => all.Contains($"step {i}\n"));
        int perSpan = (int)(RecentLogFile.SegmentSpan.TotalSeconds / 10);
        Assert.InRange(kept, perSpan, perSpan * 2);
        // What is kept is the newest run, with no hole in it.
        Assert.All(Enumerable.Range(54 - kept, kept), i => Assert.Contains($"step {i}\n", all));
        // Each file stands on its own.
        Assert.StartsWith("# header\n", File.ReadAllText(PathA));
        Assert.StartsWith("# header\n", File.ReadAllText(PathB));
        Assert.Equal(2, file.CurrentFiles.Count);
    }

    [Fact]
    public async Task NeverPassesTheSizeCap_HoweverFastTheLogGrows()
    {
        await using RecentLogFile file = Make();
        string big = new('x', 4000);

        // 4 MB inside one second: the time bound never fires, the size bound must.
        for (int i = 0; i < 1000; i++) file.Enqueue(Entry(T0.AddMilliseconds(i), big));
        await file.WhenWrittenAsync();

        Assert.True(new FileInfo(PathA).Length <= RecentLogFile.SegmentMaxBytes);
        Assert.True(new FileInfo(PathB).Length <= RecentLogFile.SegmentMaxBytes);
    }

    [Fact]
    public async Task NothingIsWrittenOnTheThreadThatLogs()
    {
        ConcurrentBag<int> writerThreads = new();
        await using RecentLogFile file = Make(open: _ => new ThreadRecordingWriter(writerThreads));
        int loggingThread = Environment.CurrentManagedThreadId;

        for (int i = 0; i < 200; i++) file.Enqueue(Entry(T0.AddSeconds(i), $"step {i}"));
        await file.WhenWrittenAsync();

        Assert.NotEmpty(writerThreads);
        Assert.DoesNotContain(loggingThread, writerThreads);
    }

    [Fact]
    public async Task AutoCollectLogsOn_WritesNoFile_AndResumesWhenItGoesOff()
    {
        LogDiagnosticState diagnostics = new() { AutoCollectLogs = true };
        await using RecentLogFile file = Make(diagnostics);

        file.Enqueue(Entry(T0, "while the full log is the record"));
        await file.WhenWrittenAsync();
        Assert.False(File.Exists(PathA));
        Assert.False(File.Exists(PathB));
        Assert.Empty(file.CurrentFiles);

        diagnostics.AutoCollectLogs = false;
        file.Enqueue(Entry(T0.AddSeconds(1), "back on"));
        await file.WhenWrittenAsync();
        Assert.Contains("back on", File.ReadAllText(Assert.Single(file.CurrentFiles)));
    }

    [Fact]
    public async Task LogServiceEntries_ReachTheFile()
    {
        LogService log = new();
        await using RecentLogFile file = new(log, new LogDiagnosticState(), PathA, PathB, () => ["# header"]);

        log.Warn("Telnet", "link dropped");
        await file.WhenWrittenAsync();

        Assert.Contains("[Warn] Telnet: link dropped", File.ReadAllText(Assert.Single(file.CurrentFiles)));
    }

    [Fact]
    public void Prune_KeepsTheNewestProcessesPairsAndLeavesOtherLogsAlone()
    {
        string other = Path.Combine(_dir, "2026-10-01_00-00-00-p9-program.log");
        File.WriteAllText(other, "x");
        for (int p = 1; p <= 5; p++)
        {
            foreach (string half in new[] { "a", "b" })
            {
                string path = Path.Combine(_dir, $"2026-10-0{p}_00-00-00-p{p}-recent-{half}.log");
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, p, 0, 0, 0, DateTimeKind.Utc));
            }
        }

        int deleted = RecentLogFile.PruneOldProcesses(_dir, keep: 3, isLiveClient: _ => false,
            nowUtc: new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(4, deleted);
        string[] left = Directory.GetFiles(_dir, "*-recent-?.log").Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(6, left.Length);
        Assert.All(left, name => Assert.DoesNotMatch("-p[12]-", name));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Prune_LeavesARunningClientsPair_AndOneWrittenInTheLastFewMinutes()
    {
        DateTime now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        void Pair(int pid, DateTime written)
        {
            foreach (string half in new[] { "a", "b" })
            {
                string path = Path.Combine(_dir, $"2026-10-01_00-00-00-p{pid}-recent-{half}.log");
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, written);
            }
        }
        Pair(101, now.AddDays(-3));        // old, its client gone: goes
        Pair(202, now.AddDays(-2));        // old, but its client is still running: stays
        Pair(303, now.AddMinutes(-2));     // the newest: the one pair within keep
        Pair(404, now.AddDays(-1));        // old, its client gone: goes
        Pair(505, now.AddMinutes(-5));     // written minutes ago: stays whatever its number

        int deleted = RecentLogFile.PruneOldProcesses(_dir, keep: 1, isLiveClient: pid => pid == 202, nowUtc: now);

        Assert.Equal(4, deleted);
        string left = string.Join(" ", Directory.GetFiles(_dir, "*-recent-?.log").Select(Path.GetFileName).Order());
        Assert.DoesNotContain("-p101-", left);
        Assert.DoesNotContain("-p404-", left);
        Assert.Contains("-p202-", left);
        Assert.Contains("-p303-", left);
        Assert.Contains("-p505-", left);
        Assert.Equal(202, RecentLogFile.ProcessIdOf("2026-10-01_00-00-00-p202-recent"));
    }

    // A disk that has stalled: the thread that logs is never held, the queue stops
    // at its depth by letting the oldest go, and the file says how many went.
    [Fact]
    public async Task AStalledDisk_NeverHoldsTheLogger_AndTheFileSaysHowManyEntriesWereDropped()
    {
        StallingWriter writer = new();
        await using RecentLogFile file = Make(open: _ => writer);
        const int extra = 500;

        // The pump takes the first entry and sticks in the write.
        file.Enqueue(Entry(T0, "step 0"));
        Assert.True(writer.Stuck.Wait(TimeSpan.FromSeconds(10)));

        // Everything logged meanwhile returns at once: this loop finishing at all,
        // with the writer still stuck, is the logger not being held.
        int total = 1 + RecentLogFile.QueueDepth + extra;
        for (int i = 1; i < total; i++) file.Enqueue(Entry(T0.AddMilliseconds(i), $"step {i}"));
        Assert.True(writer.Stuck.IsSet && !writer.Released.IsSet);

        writer.Released.Set();
        await file.WhenWrittenAsync();

        string text = writer.Text;
        System.Text.RegularExpressions.Match note = Assert.Single(
            System.Text.RegularExpressions.Regex.Matches(text, @"^# (\d+) entries dropped while the disk was busy$",
                System.Text.RegularExpressions.RegexOptions.Multiline));
        int dropped = int.Parse(note.Groups[1].Value);
        // The barrier this test waits on may itself have pushed one more out.
        Assert.InRange(dropped, extra, extra + 1);
        int written = System.Text.RegularExpressions.Regex.Matches(text, @"Walker: step \d+\n").Count;
        Assert.Equal(total, written + dropped);
        // What was let go is the oldest waiting; the end of the log is all there.
        Assert.Contains("step 0\n", text);
        Assert.DoesNotContain("Walker: step 1\n", text);
        Assert.Contains($"step {total - 1}\n", text);
        Assert.True(text.IndexOf("entries dropped", StringComparison.Ordinal)
            < text.IndexOf($"step {total - 1}\n", StringComparison.Ordinal));
    }

    // A failure nobody planned for: the pump notes it once in the program log,
    // keeps that note out of the file that failed, and writes again after a wait.
    [Fact]
    public async Task AnUnexpectedFailure_IsNotedOnce_DoesNotComeBackThroughTheFile_AndWritingResumes()
    {
        LogService log = new();
        int opens = 0;
        await using RecentLogFile file = new(log, new LogDiagnosticState(), PathA, PathB, () => ["# header"],
            open: path =>
            {
                if (Interlocked.Increment(ref opens) <= 2) throw new InvalidOperationException("no writer today");
                return new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
            },
            retryDelay: TimeSpan.FromMilliseconds(20));

        log.Info("Walker", "first");
        await file.WhenWrittenAsync();
        log.Info("Walker", "second");
        await file.WhenWrittenAsync();
        Assert.Empty(file.CurrentFiles);

        // Past the two failures the pump is still there and writes.
        log.Info("Walker", "third");
        await file.WhenWrittenAsync();

        LogEntry notice = Assert.Single(log.Snapshot(), e => e.Source == RecentLogFile.LogSource);
        Assert.Equal(LogSeverity.Warn, notice.Severity);
        Assert.Contains("InvalidOperationException: no writer today", notice.Message);
        string written = File.ReadAllText(Assert.Single(file.CurrentFiles));
        Assert.Contains("Walker: third", written);
        Assert.Contains("# writing failed (InvalidOperationException) and starts again here", written);
        Assert.DoesNotContain(RecentLogFile.LogSource, written);
    }

    // A disk error still ends the file for the session, as before, but no longer
    // without a word.
    [Fact]
    public async Task ADiskError_GivesTheFileUpForTheSession_AndSaysSoOnce()
    {
        LogService log = new();
        await using RecentLogFile file = new(log, new LogDiagnosticState(), PathA, PathB, () => ["# header"],
            open: _ => throw new IOException("disk full"));

        log.Info("Walker", "first");
        log.Info("Walker", "second");
        await file.WhenWrittenAsync();

        LogEntry notice = Assert.Single(log.Snapshot(), e => e.Source == RecentLogFile.LogSource);
        Assert.Contains("no longer written this session (IOException: disk full)", notice.Message);
        Assert.Empty(file.CurrentFiles);
    }

    // Stuck in its first write until released; everything written is kept.
    private sealed class StallingWriter : TextWriter
    {
        private readonly StringBuilder _text = new();
        public ManualResetEventSlim Stuck { get; } = new();
        public ManualResetEventSlim Released { get; } = new();
        public override Encoding Encoding => Encoding.UTF8;
        public string Text { get { lock (_text) { return _text.ToString(); } } }

        public override void Write(char value)
        {
            lock (_text) { _text.Append(value); }
        }

        public override void Write(string? value)
        {
            Stuck.Set();
            Released.Wait();
            lock (_text) { _text.Append(value); }
        }
    }

    private sealed class ThreadRecordingWriter(ConcurrentBag<int> threads) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => threads.Add(Environment.CurrentManagedThreadId);
        public override void Write(string? value) => threads.Add(Environment.CurrentManagedThreadId);
        public override void Flush() => threads.Add(Environment.CurrentManagedThreadId);
    }
}
