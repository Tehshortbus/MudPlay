using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MudPlay.Terminal;

namespace MudPlay.Services;

// The bug report's "Client and connection" section: which build is running, on
// what, since when, connected how, and what a reader has to know about the
// capture itself (which diagnostics were on, what was cleared by hand, where the
// log files are).
public static class BugReportContext
{
    public const string Heading = "Client and connection";

    public static string Build(AppServices svc, TerminalEmulator emulator, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(emulator);
        StringBuilder sb = new();

        Kv(sb, "Build", ClientEnvironment.Build);
        // The first thing to check against the date of a fix: a client left
        // running through an update is still the old code.
        Kv(sb, "Client started", $"{ClientEnvironment.ProcessStart:yyyy-MM-dd HH:mm:ss} "
            + $"(up {BugReportHoldsAndWaits.Age(now - ClientEnvironment.ProcessStart)}), process {ClientEnvironment.ProcessId}");
        Kv(sb, "Operating system", $"{ClientEnvironment.Os} ({ClientEnvironment.Architecture})");
        Kv(sb, "Desktop and window system", ClientEnvironment.Desktop);
        Kv(sb, "Windows drawn with", ClientEnvironment.Drawing);
        Kv(sb, ".NET runtime", ClientEnvironment.Runtime);
        Kv(sb, "Time zone", $"{TimeZoneInfo.Local.Id} (UTC{now:zzz}); every time in this report is local unless it ends in Z");
        Part(sb, () => Kv(sb, "Memory", $"{GC.GetTotalMemory(false) / (1024 * 1024)} MB managed, "
            + $"{Environment.WorkingSet / (1024 * 1024)} MB working set"));
        // The same figures the memory log samples and a hang report carries.
        Part(sb, () =>
        {
            foreach ((string key, string value) in ProcessResources.Read(svc.Performance.GcHandleCount, renderer: true)
                         .ReportLines(svc.ContentSizes.Describe(), svc.Activity.Read()))
                Kv(sb, key, value);
        });
        // Whether this client opened every installed font itself, which stays in
        // its memory, or took the lists from the saved catalogue.
        Part(sb, () => Kv(sb, "Installed fonts",
            $"{InstalledFontCatalog.Monospace.Count} monospace, {InstalledFontCatalog.Text.Count} text; "
            + InstalledFontCatalog.Source));
        // What keeps the client's memory down: the last compaction and what it gave
        // back, how the Loops folder is followed (a walk per change, not per few
        // seconds), and whether the program runs unpacked (Windows) or from inside
        // its own file.
        Part(sb, () =>
        {
            string compaction = svc.Memory.LastCompaction is { } c
                ? $"last compaction {c.At:HH:mm:ss} ({c.Reason}), GC committed {c.CommittedBeforeMb:0.0}→{c.CommittedAfterMb:0.0} MB, paused {c.PauseMs:0} ms"
                : "no compaction yet";
            Game.Map.NavFolderWatcher nav = svc.NavWatcher;
            Kv(sb, "Memory maintenance",
                $"{compaction}; next idle one at least {svc.Memory.CompactionInterval.TotalMinutes:0} min after it. "
                + $"Loops folder: {nav.Mode}, last check {Stamp(nav.LastCheckAt)}, last walk {Stamp(nav.LastWalkAt)}. "
                + $"Program: {Update.BundleExtraction.Describe(Update.BundleExtraction.Current(), HomeRelative(AppContext.BaseDirectory))}"
                + (svc.UnpackedCopy is { } hold ? $"; {hold.Describe()}" : string.Empty));
        });

        Part(sb, () =>
        {
            AppServices.ConnectionSnapshot link = svc.Connection;
            Kv(sb, "Connection", (link.Connected ? "connected" : link.Connecting ? "connecting" : "not connected")
                + (link.ReconnectPending ? ", a reconnect is pending" : string.Empty)
                + (svc.InGameCapture.ConnectedAt is { } up ? $"; link up since {up:HH:mm:ss} ({BugReportHoldsAndWaits.Age(now - up)})" : string.Empty));
            // "It sat there" may be a link that died: when, how long the server
            // had been silent, what ended the read, and whether a redial was armed.
            Kv(sb, "Last link drop", link.LastLoss?.Summary(now) ?? "(none this session that wasn't asked for)");
            Kv(sb, "In the game", svc.InGameCapture.InGameSince is { } entered
                ? $"since {entered:HH:mm:ss} ({BugReportHoldsAndWaits.Age(now - entered)})"
                : svc.InGameCapture.AtBoardMenu ? "no: at the board's menu" : "no");
        });

        Part(sb, () =>
        {
            TerminalScreen screen = emulator.Screen;
            Models.Settings.BbsProfile? bbs = svc.ResolveActiveBbs();
            Kv(sb, "Terminal", $"{screen.Cols}×{screen.Rows} now"
                + (bbs is null ? string.Empty : $"; the BBS is set to {bbs.TerminalCols}×{bbs.TerminalRows}, "
                    + $"{bbs.ScrollbackLines} backscroll rows"));
        });

        Part(sb, () => Kv(sb, "Schema versions",
            $"profile {svc.Profile.Current?.SchemaVersion.ToString() ?? "(no profile)"} (this build writes "
            + $"{Models.Profile.CharacterProfile.CurrentSchemaVersion}), global settings {svc.Settings.Current.SchemaVersion}"));

        Part(sb, () =>
        {
            LogDiagnosticState d = svc.LogDiagnostics;
            static string OnOff(bool on) => on ? "on" : "off";
            Kv(sb, "Log pane toggles", $"Debug {OnOff(d.DebugDiagnostics)}, Combat {OnOff(d.CombatDiagnostics)}, "
                + $"Auto-collect logs {OnOff(d.AutoCollectLogs)}, hop timing {OnOff(d.HopTiming)}, "
                + $"capture unrecognized messages {OnOff(d.CaptureUnrecognizedMessages)}, "
                + $"session statistics {OnOff(d.LogSessionStatistics)}");
            Kv(sb, "Cleared by hand", "program log: " + (svc.Log.LastClearedAt is { } log ? log.ToString("HH:mm:ss") : "no")
                + "; Wire Inspector: " + (svc.Wire.LastClearedAt is { } wire ? wire.ToString("HH:mm:ss") : "no"));
        });

        Part(sb, () => LogFiles(sb, svc));
        Part(sb, () => Kv(sb, "Open windows", OpenWindows()));
        Part(sb, () => Kv(sb, $"Freezes this session (UI thread silent {HangReport.Seconds(PerformanceMonitor.HangAfter)} or more)",
            svc.Performance.FreezesThisSession));
        Part(sb, () => GameData(sb, svc));
        Part(sb, () => OtherSettings(sb, svc));
        return sb.ToString();
    }

    // Where the files are that a hang or a forced kill leaves behind, since no
    // report can be made of a client that is no longer running.
    private static void LogFiles(StringBuilder sb, AppServices svc)
    {
        Kv(sb, "Log folder", HomeRelative(AppPaths.LogsDir));
        Kv(sb, "After a hang or a forced kill, send", svc.LogDiagnostics.AutoCollectLogs
            ? "the full program log in that folder (Auto-collect logs is on): "
              + (svc.ProgramLog.CurrentPath is { } full ? Path.GetFileName(full) : "(not open)")
            : "the last-minutes log: " + (svc.RecentLog.CurrentFiles is { Count: > 0 } files
                ? string.Join(" and ", files.Select(Path.GetFileName))
                : "(not written yet)"));
        if (svc.MemoryLog.CurrentPath is { } memory)
            Kv(sb, "Memory log", Path.GetFileName(memory));
    }

    // Every window of the client, as titled. Many things are only shown, and a
    // few only logged, while their window is open.
    private static string OpenWindows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return "(not a desktop session)";
        List<string> titles = new();
        foreach (Window window in desktop.Windows)
        {
            string title = string.IsNullOrWhiteSpace(window.Title) ? window.GetType().Name : window.Title!;
            titles.Add(title
                + (window.WindowState == WindowState.Minimized ? " (minimized)" : string.Empty)
                + (window.IsActive ? " (focused)" : string.Empty));
        }
        return titles.Count == 0 ? "(none)" : string.Join(" | ", titles);
    }

    // What identifies the game data beyond its set's name: two installs of the
    // "same" set can differ by a re-import or an edit.
    private static void GameData(StringBuilder sb, AppServices svc)
    {
        string? set = svc.GameData.ActiveSet;
        if (string.IsNullOrEmpty(set)) { Kv(sb, "Game data files", "(no active set)"); return; }
        string dir = AppPaths.GameDataSetDir(set);
        if (!Directory.Exists(dir)) { Kv(sb, "Game data files", $"{set}: its folder is missing"); return; }

        // The learned arrival rooms are kept in the set's folder and are not one of
        // its tables: what the client saw, not what was imported.
        string learned = Path.GetFileName(AppPaths.LearnedArrivalRoomsFile(set));
        List<FileInfo> files = new DirectoryInfo(dir).EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
            .Where(f => !string.Equals(f.Name, learned, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Kv(sb, "Game data files", $"{set}: {files.Count} table file(s), {files.Sum(f => f.Length) / 1024} KB, "
            + (files.Count == 0 ? "none" : $"written {files.Min(f => f.LastWriteTime):yyyy-MM-dd HH:mm} to {files.Max(f => f.LastWriteTime):yyyy-MM-dd HH:mm}")
            + $"; {svc.RoomGraph.RoomCount} room(s) loaded in the map");
        if (files.Count > 0)
            Kv(sb, "Game data tables (file, KB, written)", string.Join("; ",
                files.Select(f => $"{Path.GetFileNameWithoutExtension(f.Name)} {f.Length / 1024} {f.LastWriteTime:MM-dd HH:mm}")));
    }

    // Settings that live outside the tab dictionaries the settings sections dump.
    private static void OtherSettings(StringBuilder sb, AppServices svc)
    {
        Models.Settings.GlobalSettings g = svc.Settings.Current;
        Kv(sb, "Global settings outside the tabs",
            $"token routes {(g.EnableTokenRoutes ? "on" : "off")} (worth it when {g.TokenRouteMinRoomsShorter}+ rooms shorter; "
            + $"with an incomplete party: {(g.TokenUseWhenPartyIncomplete ? "used" : "not used")}), "
            + $"player cleanup after {g.PlayerCleanupDays} day(s), default game data {g.DefaultGameDataSet ?? "(none)"}");
        Models.Settings.BbsProfile? bbs = svc.ResolveActiveBbs();
        if (bbs is not null)
            Kv(sb, "BBS cleanup", $"every {bbs.CleanupPeriodMinutes} min, reconnect after it: {(bbs.ReconnectAfterCleanup ? "yes" : "no")}");
        if (svc.ResolveActiveRealm()?.Realm is { } realm)
            Kv(sb, "Realm", $"enter with `{realm.GameEntryCommand}`, leave with `{realm.GameExitCommand}`, "
                + $"dies at {realm.PlayerDiesAtHp} HP (refined from deaths: {(realm.AutoRefineDeathFloor ? "yes" : "no")}), "
                + $"cleanup at {realm.CleanupTimeOfDay} {realm.CleanupTimeZoneId}");
    }

    // A path under the user's home folder is written with ~: the account name on
    // the machine is nobody's business on a public issue.
    public static string HomeRelative(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }

    private static string Stamp(DateTimeOffset? at) =>
        at is { } t ? t.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) : "never";

    private static void Part(StringBuilder sb, Action write)
    {
        try { write(); }
        catch (Exception ex) { sb.Append("- _(could not read: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(")_\n"); }
    }

    private static void Kv(StringBuilder sb, string key, string value) =>
        sb.Append("- **").Append(key).Append("**: ").Append(value).Append('\n');
}
