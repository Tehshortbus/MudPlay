using System.IO;
using System.Threading;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The freeze detector: PerformanceMonitor's probe, driven on a manual clock and
// timers, with a UI thread that answers only when the test says so, writing
// HangReport files into a folder of the test's own.
public sealed class HangReportTests
{
    private const string LoginPassword = "Zq8shadowpw";
    private const string CorpsePassword = "Kx3corpsepw";

    private sealed class Rig : IDisposable
    {
        public ManualClock Clock { get; } = new();
        public ManualTimers Timers { get; } = new();
        // What the probe posted to the UI thread, run when the test lets it answer.
        public List<Action> Posted { get; } = new();
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "mudplay-hang-" + Guid.NewGuid().ToString("N"));
        public PerformanceMonitor Monitor { get; }
        public LogService Log { get; } = new();
        public SentCommandTrail Sent { get; } = new();

        // The process's memory as the probe reads it, in MB, moved by the test; and
        // the threads that read it.
        public long HeapMb = 200, AllocatedMb = 1000, WorkingSetMb = 300, PrivateMb = 350;
        // The running totals the probe reads with the memory: CPU and GC pause in
        // seconds, lock waits. CPU 0 reads as a platform that gives none.
        public double CpuSeconds, GcPauseSeconds;
        public long LockContentions;
        public Action? EachSecond { get; set; }
        public List<Thread> MemoryReaders { get; } = new();
        private const long Mb = 1024 * 1024;

        private HangReport.Memory ReadMemory(long? privateBytes)
        {
            lock (MemoryReaders) MemoryReaders.Add(Thread.CurrentThread);
            return new HangReport.Memory(HeapMb * Mb, AllocatedMb * Mb, WorkingSetMb * Mb, privateBytes,
                HeapMb * Mb, (HeapMb + 50) * Mb, 3, 2, 1,
                TimeSpan.FromSeconds(CpuSeconds), TimeSpan.FromSeconds(GcPauseSeconds), LockContentions, 4, 7);
        }

        // writeAside / runAside: where the report's file work and the probe's log
        // lines go; by default each runs at once.
        public Rig(Func<bool>? onUiThread = null, Func<BugReportScrub.Scrubber>? scrubber = null, bool arm = true,
            Action<Action>? writeAside = null, Action<Action>? runAside = null,
            Func<IReadOnlyList<(string Key, string Value)>>? resources = null,
            Func<int, ThreadStacks.Capture>? readStacks = null)
        {
            // The Desktop is there already: the client never makes one.
            Directory.CreateDirectory(Folder);
            Monitor = new PerformanceMonitor(onUiThread ?? (() => true),
                new PerformanceMonitor.ProbeHost(Posted.Add, Timers.Schedule, Clock.Timestamp, () => Clock.Now, () => Folder,
                    writeAside ?? (work => work()), runAside ?? (work => work()), ReadMemory, () => PrivateMb * Mb,
                    readStacks));
            Monitor.HangSources = new HangReport.Sources(Log, Sent,
                scrubber ?? (() => BugReportScrub.Build(
                    [new(BugReportScrub.LoginPasswordLabel, LoginPassword), new(BugReportScrub.CorpsePasswordLabel, CorpsePassword)],
                    Array.Empty<string?>(), compiled: false)),
                () => [("Character", "Fujin"), ("Realm", "paradigm")], resources);
            if (arm) Monitor.ArmHangReports();
        }

        // Time passes a second at a time, the probe ticking once a second; the UI
        // thread answers nothing.
        public void Pass(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                EachSecond?.Invoke();
                Timers.Advance(TimeSpan.FromSeconds(1));
            }
        }

        // As Pass, the UI thread answering each ping as it comes.
        public void PassAnswering(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                Pass(1);
                Answer();
            }
        }

        public void Answer()
        {
            Action[] pings = Posted.ToArray();
            Posted.Clear();
            foreach (Action ping in pings) ping();
        }

        public string[] Reports =>
            Directory.Exists(Folder) ? Directory.GetFiles(Folder, HangReport.FilePrefix + "*.md") : [];

        public string Report => File.ReadAllText(Assert.Single(Reports));

        public void Dispose()
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
    }

    // With no measured scope open, the report leads with the UI thread's call stack,
    // read for the thread that armed the watch, and lists the other threads' after.
    [Fact]
    public void An_unscoped_freeze_is_named_by_the_ui_threads_call_stack()
    {
        int askedFor = 0;
        ThreadStacks.Capture Read(int ui)
        {
            askedFor = ui;
            return new ThreadStacks.Capture(
            [
                new ThreadStacks.Stack(ui, 4242, IsUi: true, null,
                    ["MudPlay.Views.Settings.BbsSectionView.MeasureOverride(Avalonia.Size)", "Avalonia.Layout.LayoutManager.ExecuteLayoutPass()"], 3),
                new ThreadStacks.Stack(9, 5151, IsUi: false, null, ["MudPlay.Game.Map.BfsMapper.BuildLayout(MudPlay.Game.Map.RoomKey)"], 0),
            ], null, TimeSpan.FromMilliseconds(420));
        }
        using Rig rig = new(readStacks: Read);

        rig.Pass(6);

        string report = rig.Report;
        Assert.Equal(Environment.CurrentManagedThreadId, askedFor);
        Assert.Contains("Its call stack when this was written, innermost first (read in 420.0 ms)", report);
        Assert.Contains("```\nMudPlay.Views.Settings.BbsSectionView.MeasureOverride(Avalonia.Size)\nAvalonia.Layout.LayoutManager.ExecuteLayoutPass()\n… 3 more frame(s)\n```", report);
        Assert.Contains("Its call stack above names the code.", report);
        Assert.Contains("## Other threads", report);
        Assert.Contains("**Thread 9** (OS 5151)", report);
        Assert.Contains("MudPlay.Game.Map.BfsMapper.BuildLayout(MudPlay.Game.Map.RoomKey)", report);
    }

    // Stacks that couldn't be read say why, and the report stands without them.
    [Fact]
    public void Stacks_that_could_not_be_read_say_why()
    {
        using Rig rig = new(readStacks: _ => new ThreadStacks.Capture([], "call stacks are read on Windows only", TimeSpan.Zero));

        rig.Pass(6);

        string report = rig.Report;
        Assert.Contains("_No call stack: call stacks are read on Windows only._", report);
        Assert.Contains("No measured scope was open", report);
        Assert.DoesNotContain("## Other threads", report);
    }

    // CPU, GC pause and lock waits over the freeze tell computing from waiting from collecting.
    [Fact]
    public void The_report_says_whether_the_client_was_busy_waiting_or_collecting()
    {
        using Rig rig = new();
        rig.CpuSeconds = 100;
        rig.GcPauseSeconds = 10;
        rig.EachSecond = () =>
        {
            rig.CpuSeconds += 1;
            rig.GcPauseSeconds += 0.5;
            rig.LockContentions += 2;
        };

        rig.Pass(6);

        string report = rig.Report;
        Assert.Contains("## Busy, waiting or collecting", report);
        Assert.Contains("- **CPU used**: 5.0 s in 5.0 s, about 1.0 cores' worth", report);
        Assert.Contains("- **GC held every thread**: 2.50 s (50% of it)", report);
        Assert.Contains("- **Waits for a lock another thread held**: 10", report);
        Assert.Contains("- **Thread pool**: 7 thread(s), 4 work item(s) queued", report);
    }

    [Fact]
    public void A_ui_thread_silent_past_the_threshold_writes_one_report()
    {
        using Rig rig = new();
        using (rig.Monitor.Measure("stuck"))
        {
            rig.Pass(5);
            Assert.Empty(rig.Reports);
            rig.Pass(1);
            Assert.Single(rig.Reports);
            rig.Pass(120);
        }

        string report = rig.Report;
        Assert.StartsWith("Hang-20261001-120006", Path.GetFileName(rig.Reports[0]));
        Assert.Contains("- **UI thread unresponsive**: 5.0 s when this was written", report);
        Assert.Contains("- **Version**: " + AppInfo.Version, report);
        Assert.Contains("- **Character**: Fujin", report);
        Assert.Contains("still not answering after 15.0 s; innermost scope `stuck`", report);
    }

    // Report paradigm-20261026-201217: the client stood still for 3 s on a room's
    // `Obvious exits:` line, and the report could name nothing past "terminal feed".
    // Each handler of a room change now runs in a scope of its own, named for it.
    [Fact]
    public void A_room_change_that_hangs_names_the_handler_holding_it_in_the_report_and_the_log()
    {
        using Rig rig = new();
        rig.Log.Diagnostics = new LogDiagnosticState { DebugDiagnostics = true };
        string set = Path.Combine(rig.Folder, "sets", "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), """
            [ { "Map Number": 1, "Room Number": 1, "Name": "Town Square", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" } ]
            """);
        GameDataCache cache = new(Path.Combine(rig.Folder, "sets"));
        cache.SwitchSet("alpha");
        Game.Map.RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        Game.Map.RoomTracker tracker = new(graph, rig.Log, performance: rig.Monitor);
        tracker.StateChanged += _ => { };
        tracker.StateChanged += Game.Map.RoomTracker.Named("slow neighbour", _ => rig.Pass(6));

        tracker.SetLocated(new Game.Map.RoomKey(1, 1));

        Assert.Contains("1. `room change slow neighbour` at `", rig.Report);
        Assert.Contains(rig.Log.Snapshot(), e => e.Severity == LogSeverity.Debug && e.Source == "RoomTracker"
            && e.Message == "room change handler 'slow neighbour' took 6000 ms");
    }

    // A room change runs some fifty handlers. Each one under a millisecond stays out
    // of the recent activity, so a step doesn't push out what ran before it; one
    // that took longer is listed like any other work.
    [Fact]
    public void A_room_change_leaves_earlier_activity_in_the_report_and_lists_only_its_slow_handlers()
    {
        using Rig rig = new();
        string set = Path.Combine(rig.Folder, "sets", "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), """
            [ { "Map Number": 1, "Room Number": 1, "Name": "Town Square", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" } ]
            """);
        GameDataCache cache = new(Path.Combine(rig.Folder, "sets"));
        cache.SwitchSet("alpha");
        Game.Map.RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        Game.Map.RoomTracker tracker = new(graph, rig.Log, performance: rig.Monitor);
        for (int i = 0; i < 60; i++)
            tracker.StateChanged += Game.Map.RoomTracker.Named($"quick {i}", _ => { });
        tracker.StateChanged += Game.Map.RoomTracker.Named("lair timers",
            _ => rig.Clock.Advance(TimeSpan.FromMilliseconds(3)));

        using (rig.Monitor.Measure("Character Info refresh")) rig.Clock.Advance(TimeSpan.FromMilliseconds(5));
        tracker.SetLocated(new Game.Map.RoomKey(1, 1));
        using (rig.Monitor.Measure("stuck")) rig.Pass(6);

        string recent = rig.Report[rig.Report.IndexOf("## Last UI-thread activity", StringComparison.Ordinal)..];
        Assert.Contains("Character Info refresh", recent);
        Assert.Contains("room change lair timers", recent);
        Assert.DoesNotContain("room change quick", recent);
    }

    [Fact]
    public void The_report_names_each_open_scope_with_its_file_member_and_line_and_what_ran_before()
    {
        using Rig rig = new();
        rig.Monitor.NoteWindowOpened("MainWindow");
        rig.Monitor.NoteWindowOpened("SettingsWindow");
        rig.Monitor.NoteWindowFocused("SettingsWindow");
        for (int i = 0; i < 3; i++)
            using (rig.Monitor.Measure("terminal feed")) rig.Clock.Advance(TimeSpan.FromMilliseconds(4));
        OpenSettingsAndHang(rig);

        string report = rig.Report;
        int outer = report.IndexOf("1. `open Settings window` at `MudPlay.Tests/HangReportTests.cs:", StringComparison.Ordinal);
        int inner = report.IndexOf("2. `Combat weapon suggestions` at `MudPlay.Tests/HangReportTests.cs:", StringComparison.Ordinal);
        Assert.True(outer > 0 && inner > outer, report);
        Assert.Contains($"`, in `{nameof(OpenSettingsAndHang)}`: open 6.0 s", report);
        Assert.Contains($"`, in `{nameof(PickCombatSection)}`: open 6.0 s", report);
        Assert.Matches(@"4\.0 ms  terminal feed  MudPlay\.Tests/HangReportTests\.cs:\d+ \(The_report_names_\w+\)  x3", report);
        Assert.Contains("- **Open**: MainWindow, SettingsWindow", report);
        Assert.Contains("- **Last opened or closed**: opening SettingsWindow at", report);
        Assert.Contains("- **Last focused**: SettingsWindow at", report);
    }

    [Fact]
    public void Each_window_opened_or_closed_goes_in_the_program_log_with_the_windows_then_open()
    {
        using Rig rig = new();
        rig.Monitor.NoteWindowOpened("MainWindow");
        rig.Monitor.NoteWindowOpened("ConversationWindow");
        rig.Monitor.NoteWindowOpened("InfoDialog");
        rig.Monitor.NoteWindowOpened("InfoDialog");
        rig.Monitor.NoteWindowClosed("ConversationWindow");

        string[] lines = rig.Log.Snapshot().Where(e => e.Source == PerformanceMonitor.WindowsLogSource)
            .Select(e => $"[{e.Severity}] {e.Message}").ToArray();
        Assert.Equal(new[]
        {
            "[Info] opened MainWindow (open: Main)",
            "[Info] opened ConversationWindow (open: Main, Conversation)",
            "[Info] opened InfoDialog (open: Main, Conversation, InfoDialog)",
            "[Info] opened InfoDialog (open: Main, Conversation, InfoDialog x2)",
            "[Info] closed ConversationWindow (open: Main, InfoDialog x2)",
        }, lines);
        Assert.Equal("Main, InfoDialog x2", rig.Monitor.OpenWindowsSummary);
    }

    [Fact]
    public void The_report_carries_the_figures_beyond_the_managed_heap_the_bug_report_shows()
    {
        const long mb = 1024 * 1024;
        ContentSizes contents = new();
        List<int> rows = [1, 2, 3];
        IDisposable tracked = contents.Track("convo", () => rows.Count);
        contents.Track("scrollback", () => 4000, () => 4000);
        ActivityCounters activity = new();
        activity.LineReceived();
        activity.TerminalRendered();
        activity.TerminalRendered();
        ProcessResources resources = new(2000 * mb, 500 * mb, 20 * mb, 150 * mb, 60 * mb, 5 * mb, 1 * mb,
            PinnedObjects: 7, GcHandles: null, Threads: 42, Handles: 310, PoolThreads: 6, Timers: 25,
            GdiObjects: 900, UserObjects: 120, SkiaCacheBytes: null, SkiaCacheLimit: null,
            FontCacheBytes: null, FontCacheLimit: null, FontCacheEntries: null);
        using Rig rig = new(resources: () => resources.ReportLines(contents.Describe(), activity.Read()));

        using (rig.Monitor.Measure("stuck")) rig.Pass(6);

        string report = rig.Report;
        Assert.Contains("- **Native memory (outside the GC's committed memory)**: 1.5 GB, private bytes less GC committed: "
            + "private 2.0 GB, GC committed 500 MB", report);
        Assert.Contains("- **Threads and handles**: 42 threads (6 in the pool), 310 OS handles, "
            + "? (heard only while Auto-collect logs is on) GC handles, 7 pinned objects, 25 timers", report);
        Assert.Contains("- **GDI / USER objects**: 900 / 120", report);
        Assert.Contains("- **Window contents**: convo=3 scrollback=4000/4000", report);
        Assert.Contains("- **Activity this session**: 1 line received, 2 terminal renders, 0 map redraws", report);
        Assert.True(report.IndexOf("## Memory", StringComparison.Ordinal) < report.IndexOf("**Window contents**", StringComparison.Ordinal));

        // The Conversation window closing takes its rows out of the reports.
        tracked.Dispose();
        Assert.Equal("scrollback=4000/4000", contents.Describe());
    }

    private static void OpenSettingsAndHang(Rig rig)
    {
        using (rig.Monitor.Measure("open Settings window")) PickCombatSection(rig);
    }

    private static void PickCombatSection(Rig rig)
    {
        using (rig.Monitor.Measure("Combat weapon suggestions")) rig.Pass(6);
    }

    [Fact]
    public void Recovery_is_added_to_the_report_and_logged()
    {
        using Rig rig = new();
        rig.Pass(7);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Answer();                       // at 8 s; the ping went out at 1 s
        rig.Timers.Advance(TimeSpan.FromSeconds(1));

        Assert.Contains("the UI thread answered again. The hang lasted 7.0 s (", rig.Report);
        Assert.Equal("1: #1 ended 12:00:08 after 7.0 s, peak working set 300 MB, Hang-20261001-120006.md", rig.Monitor.FreezesThisSession);
        Assert.Contains(rig.Log.Snapshot(), e => e.Severity == LogSeverity.Warn
            && e.Message.StartsWith("The UI thread answered again after 7.0 s unresponsive; see Hang-", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_is_written_below_the_threshold_or_before_the_window_opens()
    {
        using Rig rig = new();
        rig.Pass(5);                        // the ping has waited 4 s
        rig.Answer();
        rig.PassAnswering(60);
        Assert.Empty(rig.Reports);

        using Rig starting = new(arm: false);
        starting.Pass(30);
        Assert.Empty(starting.Reports);
    }

    [Fact]
    public void A_probe_that_was_itself_stopped_does_not_call_it_a_hang()
    {
        using Rig rig = new();
        rig.Pass(1);                        // a ping in flight
        rig.Clock.Advance(TimeSpan.FromSeconds(30));   // the machine slept
        rig.Pass(2);
        Assert.Empty(rig.Reports);
    }

    [Fact]
    public void A_later_hang_is_a_new_report_but_not_within_the_gap()
    {
        using Rig rig = new();
        rig.Pass(7);
        rig.Answer();
        rig.Pass(10);                       // hangs again at once: inside the gap
        rig.Answer();
        rig.PassAnswering(1);
        Assert.Single(rig.Reports);
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.Contains("(no Hang file: one was written less than 2 minutes before)", StringComparison.Ordinal));

        rig.PassAnswering((int)PerformanceMonitor.ReportGap.TotalSeconds);
        rig.Pass(7);
        Assert.Equal(2, rig.Reports.Length);
    }

    [Fact]
    public void Account_details_and_corpse_passwords_never_reach_the_file()
    {
        using Rig rig = new();
        rig.Log.Info("Telnet", $"login answered with {LoginPassword}");
        rig.Log.Info("Chat", $"Raijin telepaths: the word is {CorpsePassword}");
        rig.Sent.Record($"recover corpse raijin {CorpsePassword}", SentCommandTrail.Origin.Engine, "Recovery", SentCommandTrail.Outcome.Sent);
        rig.Sent.Record("say hello", SentCommandTrail.Origin.Typed, null, SentCommandTrail.Outcome.Sent);
        rig.Pass(6);

        string report = rig.Report;
        Assert.DoesNotContain(LoginPassword, report);
        Assert.DoesNotContain(CorpsePassword, report);
        Assert.Contains(BugReportScrub.Mask, report);
        Assert.Contains("say hello", report);
        Assert.Contains("## Program log (last", report);
    }

    [Fact]
    public void Without_a_scrub_the_logs_are_left_out_and_the_code_location_stays()
    {
        using Rig rig = new(scrubber: () => throw new IOException("profile unreadable"));
        rig.Log.Info("Telnet", $"login answered with {LoginPassword}");
        using (rig.Monitor.Measure("profile save")) rig.Pass(6);

        string report = rig.Report;
        Assert.DoesNotContain(LoginPassword, report);
        Assert.DoesNotContain("## Program log", report);
        Assert.Contains("not included: the saved account details could not be read (IOException)", report);
        Assert.Contains("1. `profile save` at `MudPlay.Tests/HangReportTests.cs:", report);
    }

    [Fact]
    public void A_freeze_under_way_when_the_window_opens_counts_from_then()
    {
        using Rig rig = new(arm: false);
        rig.Pass(1);                        // the ping goes out while the client is still starting
        rig.Clock.Advance(TimeSpan.FromMilliseconds(500));
        rig.Monitor.ArmHangReports();
        rig.Pass(4);
        Assert.Empty(rig.Reports);          // 4 s since the arm
        rig.Pass(1);
        Assert.Single(rig.Reports);
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.StartsWith("Freeze watch armed: a UI thread silent 5.0 s", StringComparison.Ordinal));
    }

    [Fact]
    public void A_known_long_operation_under_its_threshold_writes_nothing()
    {
        using Rig rig = new();
        using (rig.Monitor.MeasureLong("game-data set switch", "data-Paradigm"))
        using (rig.Monitor.Measure("parse", "Rooms"))
            rig.Pass(29);
        rig.Answer();
        rig.PassAnswering(5);
        Assert.Empty(rig.Reports);
    }

    [Fact]
    public void A_known_long_operation_past_its_threshold_is_reported_as_one()
    {
        using Rig rig = new();
        using (rig.Monitor.MeasureLong("game-data set switch", "data-Paradigm")) rig.Clock.Advance(TimeSpan.FromSeconds(3));
        using (rig.Monitor.MeasureLong("game-data set switch", "data-Stock"))
        using (rig.Monitor.Measure("parse", "Rooms"))
        {
            rig.Pass(30);
            Assert.Empty(rig.Reports);
            rig.Pass(1);
        }

        string report = rig.Report;
        Assert.Contains("- **Inside a known long operation**: `game-data set switch data-Stock`, open 31.0 s: "
            + "this session it has taken up to 3.0 s, 3.0 s the last time. Such work is reported only past 30.0 s", report);
        Assert.Contains("2. `parse Rooms` at `MudPlay.Tests/HangReportTests.cs:", report);
    }

    [Fact]
    public void A_set_switch_past_the_threshold_is_not_a_hang_once_it_ends()
    {
        using Rig rig = new();
        using (rig.Monitor.MeasureLong("game-data set switch", "data-Paradigm")) rig.Pass(8);
        rig.Pass(1);                        // the work queued behind the switch, the ping still waiting
        Assert.Empty(rig.Reports);
        rig.Pass(3);
        rig.Answer();
        rig.PassAnswering(5);
        Assert.Empty(rig.Reports);
    }

    [Fact]
    public void A_freeze_after_a_long_operation_is_reported_from_its_end()
    {
        using Rig rig = new();
        using (rig.Monitor.MeasureLong("game-data set switch", "data-Paradigm")) rig.Pass(8);
        rig.Pass(4);
        Assert.Empty(rig.Reports);
        rig.Pass(1);

        Assert.Contains("- **UI thread unresponsive**: 5.0 s when this was written (since 12:00:08.000)", rig.Report);
    }

    [Fact]
    public void A_freeze_under_way_at_the_arm_leaves_the_start_out_of_its_figures()
    {
        using Rig rig = new(arm: false);
        rig.Pass(10);                       // the start keeps the UI thread from 1 s on
        rig.Monitor.ArmHangReports();
        rig.Pass(5);
        Assert.Contains("- **UI thread unresponsive**: 5.0 s when this was written (since 12:00:10.000)", rig.Report);
        rig.Pass(10);
        Assert.Contains("still not answering after 15.0 s", rig.Report);
        rig.Pass(2);
        rig.Answer();                       // at 27 s
        rig.Pass(1);

        Assert.Contains("the UI thread answered again. The hang lasted 17.0 s (", rig.Report);
    }

    [Fact]
    public void The_off_thread_scrub_masks_the_saved_account_details_and_keeps_its_copy_in_step()
    {
        ScratchRoot.Require();
        string bbs = "hang-scrub-test-" + Path.GetRandomFileName();
        string keyFile = Path.Combine(AppPaths.DataRoot, $"hang-scrub-{Guid.NewGuid():N}.credkey");
        PasswordProtector protector = new(keyFile);
        ProfileService profiles = TestProfiles.Service();
        try
        {
            profiles.CreateProfile(bbs, "Scrubbed", p =>
            {
                p.BbsCredentials = new(StringComparer.OrdinalIgnoreCase)
                {
                    [bbs] = new Models.Profile.BbsCredentials
                    {
                        EncryptedUsername = protector.Protect("Qloginname"),
                        EncryptedPassword = protector.Protect("Qloginpass1"),
                    },
                };
                p.EncryptedSuicidePassword = protector.Protect("Qsuicide1");
            });
            profiles.Load(bbs, "Scrubbed");
            // As AppServices wires it: from the profile service's copy, off the UI thread.
            BugReportScrub.Scrubber Scrubber() => BugReportBuilder.BuildScrubberOffUiThread(profiles, protector, [], null);
            string Scrub(string text) => Scrubber().Apply(text);

            Assert.Equal("[redacted] [redacted] [redacted]", Scrub("Qloginname Qloginpass1 Qsuicide1"));

            profiles.Current!.BbsCredentials![bbs].EncryptedPassword = protector.Protect("Qnewpass22");
            profiles.Save();
            Assert.Equal("Qloginpass1 [redacted]", Scrub("Qloginpass1 Qnewpass22"));

            profiles.Current.EncryptedSuicidePassword = protector.Protect("Qsuicide2");
            profiles.NotifyMutated();
            Assert.Equal("Qsuicide1 [redacted]", Scrub("Qsuicide1 Qsuicide2"));

            // And a whole Hang report through it, a password in the log tail.
            using (Rig rig = new(scrubber: Scrubber))
            {
                rig.Log.Info("Telnet", "login answered with Qnewpass22");
                rig.Pass(6);
                Assert.DoesNotContain("Qnewpass22", rig.Report);
                Assert.Contains("Telnet: login answered with [redacted]", rig.Report);
            }

            profiles.Close();
            Assert.Empty(profiles.AccountSecrets);
            Assert.Equal("Qsuicide2", Scrub("Qsuicide2"));
        }
        finally
        {
            if (Directory.Exists(AppPaths.BbsFolder(bbs))) Directory.Delete(AppPaths.BbsFolder(bbs), recursive: true);
            if (File.Exists(keyFile)) File.Delete(keyFile);
        }
    }

    [Fact]
    public void The_report_has_memory_then_and_now_with_the_rate_and_the_minutes_before()
    {
        using Rig rig = new();
        rig.EachSecond = () => rig.AllocatedMb += 20;      // a steady 20 MB/s before the hang
        rig.PassAnswering(100);
        rig.EachSecond = () => { rig.AllocatedMb += 100; rig.HeapMb += 50; rig.WorkingSetMb += 60; };
        rig.Pass(6);                                      // the ping went out at 101 s

        string report = rig.Report;
        Assert.Contains("- **Working set**: 360 MB then, 660 MB now (+300 MB)", report);
        Assert.Contains("- **Managed heap**: 250 MB then, 500 MB now (+250 MB)", report);
        Assert.Contains("- **Private bytes**: 350 MB then, 350 MB now (+0 MB)", report);
        Assert.Contains("- **Allocated since the hang began**: 500 MB, 100.0 MB/s over 5.0 s", report);
        Assert.Contains("  20.0 MB/s allocated", report);           // a row from before
        Assert.Matches(@"Before the hang[^`]*```\n(\d\d:\d\d:\d\d\.\d{3}  working set +\d+ MB  heap +\d+ MB.*\n){4}", report);
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.EndsWith(
            "; working set 660 MB (+300 MB since it stopped), heap 500 MB, 100.0 MB/s allocated", StringComparison.Ordinal));
    }

    [Fact]
    public void Growth_through_a_hang_shows_in_its_milestones_and_its_recovery()
    {
        using Rig rig = new();
        rig.EachSecond = () => { rig.AllocatedMb += 100; rig.HeapMb += 50; rig.WorkingSetMb += 60; };
        rig.Pass(16);                       // the ping went out at 1 s: start heap 250, allocated 1100, working set 360
        Assert.Contains("still not answering after 15.0 s; innermost scope none; +1.5 GB allocated, heap 1000 MB, working set 1.2 GB.",
            rig.Report);
        rig.Pass(4);
        rig.Answer();                       // at 20 s
        rig.Pass(1);

        Assert.Contains("The hang lasted 19.0 s (+2.0 GB allocated, heap 1.2 GB, working set 1.5 GB).", rig.Report);
        Assert.StartsWith("1: #1 ended 12:00:21 after 19.0 s, peak working set 1.5 GB", rig.Monitor.FreezesThisSession);
    }

    [Fact]
    public void A_stall_while_collecting_is_written_with_what_the_ui_thread_was_doing()
    {
        using Rig rig = new();
        List<string> lines = new();
        rig.Monitor.CollectTo(lines.Add);
        rig.Pass(1);                        // a ping in flight; the probe now looks every 20 ms
        using (rig.Monitor.Measure("parse", "Rooms"))
            for (int i = 0; i < 10; i++)
            {
                rig.Clock.Advance(TimeSpan.FromMilliseconds(20));
                rig.Timers.Advance(TimeSpan.FromMilliseconds(20));
            }
        rig.Answer();

        Assert.Equal(["stall 200 ms  parse Rooms"], lines);
    }

    [Fact]
    public void A_sleep_inside_a_hang_ends_its_figures()
    {
        using Rig rig = new();
        rig.Pass(7);                        // reported at 6 s
        rig.Clock.Advance(TimeSpan.FromMinutes(10));   // the machine slept
        rig.Pass(1);
        rig.Answer();
        rig.PassAnswering(2);

        string report = rig.Report;
        Assert.Contains("the freeze watch was itself stopped for 601.0 s", report);
        Assert.DoesNotContain("answered again", report);
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.StartsWith("The freeze watch was itself stopped", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Log.Snapshot(), e => e.Message.StartsWith("The UI thread answered again", StringComparison.Ordinal));
    }

    [Fact]
    public void Log_lines_left_waiting_on_a_stuck_log_are_dropped_and_counted()
    {
        List<Action> held = new();
        using Rig rig = new(runAside: held.Add);
        rig.Pass(7);
        rig.Answer();
        rig.PassAnswering(1);               // recovery: its log line is out, the log not answering
        Assert.Contains("the UI thread answered again. The hang lasted", rig.Report);   // the file's own lane still has it
        rig.Pass(10);                       // a second freeze, inside the gap
        rig.Answer();
        rig.PassAnswering(1);
        Assert.Single(held);

        held[0]();                          // the log answers
        held.Clear();
        rig.PassAnswering((int)PerformanceMonitor.ReportGap.TotalSeconds);
        rig.Pass(7);
        rig.Answer();
        rig.PassAnswering(1);
        held.Single()();
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.EndsWith(
            "(1 earlier line(s) from the freeze watch dropped: the log did not answer)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_probe_goes_on_while_the_report_is_stuck_on_the_disk()
    {
        List<Action> stuck = new();
        using Rig rig = new(writeAside: stuck.Add);
        using (rig.Monitor.Measure("stuck")) rig.Pass(40);

        // The disk never answered, and the probe went on ticking: the milestones are queued behind the report.
        Assert.Empty(rig.Reports);
        Assert.Equal(3, stuck.Count);
        rig.Answer();
        rig.Pass(1);
        foreach (Action work in stuck.ToArray()) work();

        string report = rig.Report;
        Assert.Contains("still not answering after 30.0 s", report);
        Assert.True(report.IndexOf("after 15.0 s", StringComparison.Ordinal) < report.IndexOf("answered again", StringComparison.Ordinal));
    }

    [Fact]
    public void A_log_write_stuck_on_the_ui_thread_keeps_neither_the_report_nor_its_log_tail()
    {
        Thread? ui = null;
        using Rig rig = new(onUiThread: () => Thread.CurrentThread == ui);
        rig.Log.Info("Telnet", "before the stall");
        using ManualResetEventSlim entered = new(), release = new();
        // As the program log's file writer does: every entry written on the
        // logging thread, here one whose disk never answers.
        rig.Log.EntryAdded += _ =>
        {
            if (Thread.CurrentThread != ui) return;
            entered.Set();
            release.Wait();
        };
        ui = new Thread(() =>
        {
            using (rig.Monitor.Measure("program log file write")) rig.Log.Info("Combat", "the line being written");
        });
        ui.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            rig.Pass(6);
            string report = rig.Report;
            Assert.Contains("1. `program log file write` at `MudPlay.Tests/HangReportTests.cs:", report);
            Assert.Contains("[Info]  Telnet: before the stall", report);
            Assert.Contains("[Info]  Combat: the line being written", report);
        }
        finally
        {
            release.Set();
            ui.Join();
        }
    }

    [Fact]
    public void The_probe_reads_a_frozen_ui_thread_without_its_help()
    {
        Thread? ui = null;
        using Rig rig = new(onUiThread: () => Thread.CurrentThread == ui);
        using ManualResetEventSlim entered = new(), release = new();
        ui = new Thread(() =>
        {
            using (rig.Monitor.Measure("parse Rooms"))
            {
                entered.Set();
                release.Wait();
            }
        });
        ui.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            rig.Pass(6);
            string report = rig.Report;
            Assert.Contains("1. `parse Rooms` at `MudPlay.Tests/HangReportTests.cs:", report);
            // Written with the ping still waiting: nothing was asked of the UI thread,
            // and its memory was read on the probe's.
            Assert.Single(rig.Posted);
            Assert.Contains("## Memory", report);
            lock (rig.MemoryReaders) Assert.DoesNotContain(ui, rig.MemoryReaders);
        }
        finally
        {
            release.Set();
            ui.Join();
        }
    }
}
