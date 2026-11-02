using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The full program log on disk (Auto-collect logs): the thread that logs never
// waits for the disk, entries land in order, a stuck disk drops the oldest and
// the file says so, a failed write is noted once and retried, exit and a crash
// wait a bounded time for the queue, and the switch opens and closes files at
// runtime, each entry in its own session's file. Driven through LogService; the
// disk is the open seam.
public sealed class ProgramLogFileTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // Every file the log opened, in order, and where it was asked to go.
    private sealed class Disks
    {
        public ConcurrentQueue<string> Paths { get; } = new();
        public ConcurrentQueue<TestDisk> Opened { get; } = new();
        public Func<TestDisk> Make { get; init; } = () => new TestDisk();

        public TextWriter Open(string path)
        {
            Paths.Enqueue(path);
            TestDisk disk = Make();
            Opened.Enqueue(disk);
            return disk;
        }

        public string AllText => string.Concat(Opened.Select(d => d.Text));
    }

    private static ProgramLogFile Make(LogService log, Disks disks, bool on = true,
        LogDiagnosticState? diagnostics = null, TimeSpan? retryDelay = null) =>
        new(log, diagnostics ?? new LogDiagnosticState { AutoCollectLogs = on }, disks.Open, retryDelay);

    [Fact]
    public async Task LoggingNeverWaitsForAStuckDisk_AndEntriesLandInOrderOnceItAnswers()
    {
        LogService log = new();
        TestDisk disk = new() { StallFirstWrite = true };
        Disks disks = new() { Make = () => disk };
        using ProgramLogFile file = Make(log, disks);
        try
        {
            // The pump opens the file on the switch and sticks writing its header.
            Assert.True(disk.Stuck.Wait(Patience));

            Stopwatch clock = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) log.Info("Walker", $"step {i}");
            clock.Stop();
            Assert.False(disk.Released.IsSet);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"logging took {clock.Elapsed} with the disk stuck");

            disk.Released.Set();
            await file.WhenWrittenAsync().WaitAsync(Patience);

            string[] steps = Regex.Matches(disk.Text, @"Walker: step (\d+)").Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString()), steps);
            Assert.StartsWith("# MudPlay debug log — topic=program", disk.Text);
        }
        finally { disk.Released.Set(); }
    }

    [Fact]
    public async Task AStuckDisk_DropsTheOldestBeyondTheCap_AndTheFileSaysHowMany()
    {
        LogService log = new();
        TestDisk disk = new() { StallFirstWrite = true };
        Disks disks = new() { Make = () => disk };
        using ProgramLogFile file = Make(log, disks);
        const int extra = 300;
        try
        {
            Assert.True(disk.Stuck.Wait(Patience));
            int total = ProgramLogFile.QueueDepth + extra;
            for (int i = 0; i < total; i++) log.Info("Walker", $"step {i}");

            disk.Released.Set();
            await file.WhenWrittenAsync().WaitAsync(Patience);

            string text = disk.Text;
            Match note = Assert.Single(Regex.Matches(text, @"^# (\d+) entries dropped while the disk was busy\r?$", RegexOptions.Multiline));
            int dropped = int.Parse(note.Groups[1].Value);
            // The barrier this test waits on may itself have pushed one more out.
            Assert.InRange(dropped, extra, extra + 1);
            int written = Regex.Matches(text, @"Walker: step \d+").Count;
            Assert.Equal(total, written + dropped);
            // What went is the oldest; the end of the log is all there, after the note.
            Assert.DoesNotContain("Walker: step 0\n", text.Replace("\r", ""));
            Assert.Contains($"Walker: step {total - 1}", text);
            Assert.True(note.Index < text.IndexOf("Walker: step", StringComparison.Ordinal));
        }
        finally { disk.Released.Set(); }
    }

    [Fact]
    public async Task AFailedWrite_IsNotedOnce_NeverThroughItsOwnFile_AndAgainOnlyAfterAWriteSucceeds()
    {
        LogService log = new();
        int failing = 1;
        Disks disks = new() { Make = () => new TestDisk { FailFlush = () => Volatile.Read(ref failing) == 1 } };
        using ProgramLogFile file = Make(log, disks, retryDelay: TimeSpan.FromMilliseconds(20));

        // Two batches that both fail: one notice.
        log.Info("Walker", "first");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        log.Info("Walker", "second");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.True(disks.Opened.Count >= 2, "the file was not opened again after the failure");
        LogEntry notice = Assert.Single(log.Snapshot(), e => e.Source == ProgramLogFile.LogSource);
        Assert.Equal(LogSeverity.Warn, notice.Severity);
        Assert.Contains("IOException: disk full", notice.Message);

        // The disk answers: the file says where writing starts again.
        Volatile.Write(ref failing, 0);
        log.Info("Walker", "third");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        string resumed = disks.Opened.Last().Text;
        // The very first flush failed with the header in it: the reopen writes it again.
        Assert.StartsWith("# MudPlay debug log — topic=program", resumed);
        Assert.Contains("# writing failed (IOException: disk full) and starts again here: up to", resumed);
        Assert.Contains("Walker: third", resumed);
        Assert.Contains(log.Snapshot(), e => e.Source == ProgramLogFile.LogSource && e.Severity == LogSeverity.Info);

        // A write succeeded in between, so a new failure gets a notice of its own.
        Volatile.Write(ref failing, 1);
        log.Info("Walker", "fourth");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.Equal(2, log.Snapshot().Count(e => e.Source == ProgramLogFile.LogSource && e.Severity == LogSeverity.Warn));

        // The notices never reach the file they report on, and every reopen is the
        // same session's file.
        Assert.DoesNotContain(ProgramLogFile.LogSource + ":", disks.AllText);
        Assert.Single(disks.Paths.Distinct());
    }

    [Fact]
    public async Task AFailureInOneFile_IsNotCarriedIntoTheNextOne()
    {
        LogService log = new();
        LogDiagnosticState diagnostics = new() { AutoCollectLogs = true };
        int failing = 1;
        Disks disks = new() { Make = () => new TestDisk { FailFlush = () => Volatile.Read(ref failing) == 1 } };
        using ProgramLogFile file = Make(log, disks, diagnostics: diagnostics, retryDelay: TimeSpan.FromMilliseconds(20));
        int Warnings() => log.Snapshot().Count(e => e.Source == ProgramLogFile.LogSource && e.Severity == LogSeverity.Warn);

        log.Info("Walker", "first");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.Equal(1, Warnings());

        // Off and on while still failing: the new file's failure is noted too.
        diagnostics.AutoCollectLogs = false;
        diagnostics.AutoCollectLogs = true;
        log.Info("Walker", "second");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.Equal(2, Warnings());

        // Off, the disk answers, on: the new file starts clean, with no word of
        // the old one's failure.
        diagnostics.AutoCollectLogs = false;
        Volatile.Write(ref failing, 0);
        diagnostics.AutoCollectLogs = true;
        log.Info("Walker", "clean");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        string fresh = disks.Opened.Last().Text;
        Assert.StartsWith("# MudPlay debug log — topic=program", fresh);
        Assert.Contains("Walker: clean", fresh);
        Assert.DoesNotContain("writing failed", fresh);
    }

    [Fact]
    public async Task ExitDuringARetryWait_StillWritesTheTailWithinTheBound()
    {
        LogService log = new();
        int failing = 1;
        Disks disks = new() { Make = () => new TestDisk { FailFlush = () => Volatile.Read(ref failing) == 1 } };
        // The failure notice, not WhenWrittenAsync: a barrier posted after the writer
        // has taken "first" is read only after the 30 s wait. Listened for before the
        // file is made: the session's header can fail its flush, and draw the one
        // notice, before "first" is logged.
        TaskCompletionSource noticed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        log.EntryAdded += e => { if (e.Source == ProgramLogFile.LogSource) noticed.TrySetResult(); };
        // A retry wait far longer than the exit's bound.
        ProgramLogFile file = Make(log, disks, retryDelay: TimeSpan.FromSeconds(30));

        log.Info("Walker", "first");
        await noticed.Task.WaitAsync(Patience);

        Volatile.Write(ref failing, 0);
        log.Info("Walker", "the tail");
        Stopwatch clock = Stopwatch.StartNew();
        file.Dispose();
        clock.Stop();

        Assert.True(clock.Elapsed < ProgramLogFile.ExitDrainBound, $"exit took {clock.Elapsed}");
        Assert.Contains("Walker: the tail", disks.AllText);
    }

    [Fact]
    public async Task EntriesQueuedAcrossASwitch_EachLandInTheirOwnSessionsFile()
    {
        LogService log = new();
        LogDiagnosticState diagnostics = new() { AutoCollectLogs = true };
        TestDisk first = new() { StallFirstWrite = true };
        int opened = 0;
        Disks disks = new() { Make = () => Interlocked.Increment(ref opened) == 1 ? first : new TestDisk() };
        using ProgramLogFile file = Make(log, disks, diagnostics: diagnostics);
        try
        {
            // The first file is stuck: everything below waits in the queue.
            Assert.True(first.Stuck.Wait(Patience));
            log.Info("Walker", "in the first file");
            diagnostics.AutoCollectLogs = false;
            log.Info("Walker", "while off");
            diagnostics.AutoCollectLogs = true;
            log.Info("Walker", "in the second file");

            first.Released.Set();
            await file.WhenWrittenAsync().WaitAsync(Patience);

            Assert.Equal(2, disks.Opened.Count);
            TestDisk second = disks.Opened.Last();
            Assert.Contains("Walker: in the first file", first.Text);
            Assert.DoesNotContain("in the second file", first.Text);
            Assert.StartsWith("# MudPlay debug log — topic=program", second.Text);
            Assert.Contains("Walker: in the second file", second.Text);
            Assert.DoesNotContain("in the first file", second.Text);
            Assert.DoesNotContain("while off", disks.AllText);
            Assert.True(first.Disposed);
        }
        finally { first.Released.Set(); }
    }

    // Loggers racing the switch: an entry logged as Auto-collect flips must go to
    // its own session, never start that session's file over again (a second
    // header for it) after the next session has begun. Bounded by counts, not
    // time: the switch and the loggers keep pace in windows of `window` entries.
    // Left to run free, loggers whose code the rest of the suite had already
    // made hot could finish before the switch had flipped twice, and the test
    // failed for want of a race; kept in step, every flip lands while they are
    // logging however the threads are scheduled, and no session holds so many
    // entries that the queue's cap could push every other session out.
    [Fact]
    public async Task LoggersRacingTheSwitch_NeverStartASessionsFileTwice()
    {
        LogService log = new();
        LogDiagnosticState diagnostics = new() { AutoCollectLogs = true };
        Disks disks = new();
        using ProgramLogFile file = Make(log, disks, diagnostics: diagnostics);
        const int loggers = 3, flips = 3000, window = 8;
        int logged = 0, flipped = 0, done = 0;
        using Barrier start = new(loggers + 1);

        Thread[] threads = Enumerable.Range(0, loggers).Select(n => new Thread(() =>
        {
            start.SignalAndWait();
            for (int i = 0; Volatile.Read(ref done) == 0; i++)
            {
                // At most a window past the latest flip.
                SpinWait ahead = default;
                while (Volatile.Read(ref done) == 0 && Volatile.Read(ref logged) >= (Volatile.Read(ref flipped) + 1) * window)
                    ahead.SpinOnce(sleep1Threshold: -1);
                log.Info("Walker", $"logger {n} step {i}");
                Interlocked.Increment(ref logged);
            }
        })).ToArray();
        foreach (Thread thread in threads) thread.Start();
        start.SignalAndWait();
        try
        {
            for (int t = 0; t < flips; t++)
            {
                // Half a window before the loggers would have to stop for it:
                // they are still logging as the switch flips.
                SpinWait behind = default;
                while (Volatile.Read(ref logged) < (t + 1) * window - window / 2) behind.SpinOnce(sleep1Threshold: -1);
                diagnostics.AutoCollectLogs = !diagnostics.AutoCollectLogs;
                Volatile.Write(ref flipped, t + 1);
            }
        }
        finally
        {
            Volatile.Write(ref done, 1);
            foreach (Thread thread in threads) thread.Join();
        }
        diagnostics.AutoCollectLogs = true;
        await file.WhenWrittenAsync().WaitAsync(Patience);

        TestDisk[] opened = disks.Opened.ToArray();
        Assert.True(opened.Length > 2, $"only {opened.Length} files were opened over {flips} flips");
        Assert.All(opened, disk =>
            Assert.True(Regex.Matches(disk.Text, "^# MudPlay debug log", RegexOptions.Multiline).Count <= 1,
                "a file was given two headers"));
        string[] starts = opened.SelectMany(d => Regex.Matches(d.Text, @"^# started (\S+)", RegexOptions.Multiline))
            .Select(m => m.Groups[1].Value).ToArray();
        string[] repeated = starts.GroupBy(s => s).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.True(repeated.Length == 0, $"{repeated.Length} sessions' files were started again");
    }

    [Fact]
    public void Exit_WaitsForASlowDisk_ButNoLongerThanTheBoundForAStuckOne()
    {
        // Slow: answers after a moment, inside the bound, and everything lands.
        LogService slowLog = new();
        TestDisk slow = new() { StallFirstWrite = true };
        ProgramLogFile slowFile = Make(slowLog, new Disks { Make = () => slow });
        Assert.True(slow.Stuck.Wait(Patience));
        for (int i = 0; i < 50; i++) slowLog.Info("Walker", $"step {i}");
        using Timer answer = new(_ => slow.Released.Set(), null, 300, Timeout.Infinite);
        slowFile.Dispose();
        Assert.Contains("Walker: step 49", slow.Text);

        // Stuck for good: exit goes on once the bound is up.
        LogService stuckLog = new();
        TestDisk stuck = new() { StallFirstWrite = true };
        ProgramLogFile stuckFile = Make(stuckLog, new Disks { Make = () => stuck });
        try
        {
            Assert.True(stuck.Stuck.Wait(Patience));
            stuckLog.Info("Walker", "never written");
            Stopwatch clock = Stopwatch.StartNew();
            stuckFile.Dispose();
            clock.Stop();
            Assert.InRange(clock.Elapsed, ProgramLogFile.ExitDrainBound - TimeSpan.FromMilliseconds(100),
                ProgramLogFile.ExitDrainBound + TimeSpan.FromSeconds(2));
        }
        finally { stuck.Released.Set(); }
    }

    [Fact]
    public void TheCrashWait_LandsWhatWasLogged_AndGivesUpOnAStuckDiskAtItsBound()
    {
        LogService log = new();
        TestDisk disk = new() { StallFirstWrite = true };
        using ProgramLogFile file = Make(log, new Disks { Make = () => disk });
        try
        {
            Assert.True(disk.Stuck.Wait(Patience));
            log.Error("Combat", "the line before the crash");

            Stopwatch clock = Stopwatch.StartNew();
            Assert.False(file.WaitWritten(TimeSpan.FromMilliseconds(300)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the stuck wait took {clock.Elapsed}");

            disk.Released.Set();
            Assert.True(file.WaitWritten(ProgramLogFile.CrashDrainBound));
            Assert.Contains("[Error] Combat: the line before the crash", disk.Text);
        }
        finally { disk.Released.Set(); }
    }

    [Fact]
    public async Task TheSwitch_OpensAFreshFileEachTimeItGoesOn_AndClosesItWhenOff()
    {
        LogService log = new();
        LogDiagnosticState diagnostics = new();
        Disks disks = new();
        using ProgramLogFile file = Make(log, disks, diagnostics: diagnostics);

        log.Info("Walker", "before");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.Empty(disks.Opened);
        Assert.Null(file.CurrentPath);

        diagnostics.AutoCollectLogs = true;
        log.Info("Walker", "during");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        TestDisk first = Assert.Single(disks.Opened);
        string firstPath = Assert.Single(disks.Paths);
        Assert.Equal(firstPath, file.CurrentPath);
        Assert.StartsWith(AppPaths.LogsDir, firstPath);
        Assert.EndsWith($"-p{Environment.ProcessId}-program.log", firstPath);

        diagnostics.AutoCollectLogs = false;
        Assert.Null(file.CurrentPath);
        log.Info("Walker", "while off");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.True(first.Disposed);
        Assert.Contains("Walker: during", first.Text);
        Assert.DoesNotContain("while off", disks.AllText);

        diagnostics.AutoCollectLogs = true;
        log.Info("Walker", "again");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.Equal(2, disks.Opened.Count);
        TestDisk second = disks.Opened.Last();
        Assert.StartsWith("# MudPlay debug log — topic=program", second.Text);
        Assert.Contains("Walker: again", second.Text);
        Assert.DoesNotContain("again", first.Text);
        Assert.Equal(disks.Paths.Last(), file.CurrentPath);
    }

    [Fact]
    public async Task EveryFile_OpensWithTheBuildTheProcessAndWhoseSessionItIs_AsItStandsWhenThatFileOpens()
    {
        LogService log = new();
        LogDiagnosticState diagnostics = new();
        Disks disks = new();
        string character = "Fujin";
        using ProgramLogFile file = new(log, diagnostics, disks.Open,
            session: () => new LogFileHeader.Session(character, "Mudbbs", "paradigm", "Paradigm 1.11"));

        diagnostics.AutoCollectLogs = true;
        log.Info("Walker", "first");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        diagnostics.AutoCollectLogs = false;
        character = "Raiden";
        diagnostics.AutoCollectLogs = true;
        log.Info("Walker", "second");
        await file.WhenWrittenAsync().WaitAsync(Patience);

        Assert.Equal(2, disks.Opened.Count);
        foreach ((TestDisk disk, string who) in disks.Opened.Zip(["Fujin", "Raiden"]))
        {
            string[] lines = disk.Text.Split(Environment.NewLine);
            Assert.Equal("# MudPlay debug log — topic=program", lines[0]);
            Assert.StartsWith("# started ", lines[1]);
            Assert.Equal("# MudPlay " + ClientEnvironment.Build, lines[2]);
            Assert.Contains(ClientEnvironment.Os, lines[3]);
            Assert.StartsWith($"# process {Environment.ProcessId} started ", lines[4]);
            Assert.StartsWith("# client up ", lines[5]);
            Assert.Equal($"# character {who}; BBS Mudbbs; realm paradigm; game data Paradigm 1.11", lines[6]);
        }
    }

    [Fact]
    public async Task WithTheRealDisk_TheSessionFileUnderLogsHoldsTheHeaderAndTheEntries()
    {
        ScratchRoot.Require();
        LogService log = new();
        string? path = null;
        ProgramLogFile file = new(log, new LogDiagnosticState { AutoCollectLogs = true });
        try
        {
            log.Warn("Telnet", "link dropped");
            await file.WhenWrittenAsync().WaitAsync(Patience);
            path = file.CurrentPath;
            Assert.NotNull(path);
            Assert.Equal(AppPaths.LogsDir, Path.GetDirectoryName(path));

            using FileStream stream = new(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            string text = new StreamReader(stream).ReadToEnd();
            Assert.StartsWith("# MudPlay debug log — topic=program", text);
            Assert.Matches(@"\d\d:\d\d:\d\d\.\d{3} \[Warn\] Telnet: link dropped", text);
        }
        finally
        {
            file.Dispose();
            if (path is not null) File.Delete(path);
        }
    }

    // A file that keeps what is written to it. StallFirstWrite holds the first
    // string written until Released; FailFlush makes a flush throw a disk error.
    // Also the disk under CrashReporterTests' program log.
    internal sealed class TestDisk : TextWriter
    {
        private readonly StringBuilder _text = new();
        private int _writes;
        public bool StallFirstWrite { get; init; }
        public Func<bool>? FailFlush { get; init; }
        public ManualResetEventSlim Stuck { get; } = new();
        public ManualResetEventSlim Released { get; } = new();
        public bool Disposed { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;
        public string Text { get { lock (_text) { return _text.ToString(); } } }

        public override void Write(char value)
        {
            lock (_text) { _text.Append(value); }
        }

        public override void Write(string? value)
        {
            if (StallFirstWrite && Interlocked.Increment(ref _writes) == 1)
            {
                Stuck.Set();
                Released.Wait();
            }
            lock (_text) { _text.Append(value); }
        }

        public override void Flush()
        {
            if (FailFlush?.Invoke() == true) throw new IOException("disk full");
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
