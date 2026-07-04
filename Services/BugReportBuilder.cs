using System.Text;
using System.Text.Json;
using FujinTerm.Game;
using FujinTerm.Game.Inventory;
using FujinTerm.Terminal;

namespace FujinTerm.Services;

// Snapshots the live client state into a self-contained Markdown bug report.
// Capture freezes everything time-sensitive (recent scrollback, the program
// log tail, all gameplay settings, engine + player state) at the instant the
// user clicks "Bug report", so the report reflects the moment of the problem
// rather than whenever the user finishes typing their description. Render then
// folds the user's description in and produces the final document; FileName
// derives the Desktop file name (realm-timestamp.md).
//
// The two-phase split (capture → render) keeps the capture pure data: the
// description arrives from a dialog that opens after the click, and the
// scrollback / log keep growing while the user types. Rendering per-section
// Markdown at capture time is deliberate — it freezes each subsystem's view
// without holding live references that could mutate underneath us.
public static class BugReportBuilder
{
    // How many trailing transcript lines (scrollback + live screen) to include.
    private const int ScrollbackLines = 500;

    // How many trailing program-log entries to include.
    private const int LogLines = 250;

    // One captured section of the report — a heading and its pre-rendered Markdown body.
    public readonly record struct Section(string Heading, string Body);

    // Frozen point-in-time capture produced by Capture. Holds the realm +
    // timestamp used for the file name and every pre-rendered section. The
    // user's issue description is folded in later by Render.
    public sealed record BugReportCapture(
        DateTimeOffset CapturedAt,
        RealmType Realm,
        IReadOnlyList<Section> Sections);

    // Freeze the current client state into a BugReportCapture. Every section is
    // built defensively — a failure reading one subsystem is surfaced inline in
    // that section rather than aborting the whole report, because a bug report
    // is most needed exactly when something is in a bad state.
    public static BugReportCapture Capture(AppServices svc, TerminalEmulator emulator)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(emulator);

        DateTimeOffset now = DateTimeOffset.Now;
        RealmType realm = Guard(() => svc.GameData.ActiveRealm, RealmType.Stock);

        List<Section> sections =
        [
            new("Session", SafeSection(() => BuildSession(svc, realm, now))),
            new("Player state", SafeSection(() => BuildPlayerState(svc))),
            new("Inventory", SafeSection(() => BuildInventory(svc))),
            new("Player Workshop", SafeSection(() => BuildWorkshop(svc))),
            new("Movement engine", SafeSection(() => BuildMovement(svc))),
            new("Special room markers", SafeSection(() => BuildRoomMarkers(svc))),
            new("Auto-mode", SafeSection(() => BuildAutoMode(svc))),
            new("Settings (excluding BBS + Display)", SafeSection(() => BuildSettings(svc))),
            new("Program log", SafeSection(() => BuildLog(svc))),
            new("Scrollback", SafeSection(() => BuildScrollback(emulator))),
        ];

        return new BugReportCapture(now, realm, sections);
    }

    // Compose the final Markdown document from a capture and the user's
    // issueDescription. The description is placed at the top so a triager reads
    // the "what went wrong" before the state dump.
    public static string Render(BugReportCapture capture, string issueDescription)
    {
        ArgumentNullException.ThrowIfNull(capture);

        StringBuilder sb = new(capacity: 16 * 1024);
        sb.Append("# FujinTerm bug report\n\n");
        sb.Append("_Captured ").Append(capture.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))
          .Append("  •  realm ").Append(RealmLabel(capture.Realm)).Append("_\n\n");

        sb.Append("## Issue\n\n");
        sb.Append(string.IsNullOrWhiteSpace(issueDescription) ? "_(none provided)_" : issueDescription.Trim());
        sb.Append("\n\n");

        foreach (Section section in capture.Sections)
        {
            sb.Append("## ").Append(section.Heading).Append("\n\n");
            sb.Append(section.Body.TrimEnd()).Append("\n\n");
        }

        return sb.ToString();
    }

    // Desktop file name for a capture: realm-yyyyMMdd-HHmmss.md, e.g.
    // paradigm-20260703-142530.md. Uses the click timestamp so the name matches
    // when the problem was seen, not when the file was written.
    public static string FileName(BugReportCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return $"{RealmLabel(capture.Realm)}-{capture.CapturedAt:yyyyMMdd-HHmmss}.md";
    }

    // ----- Section builders ----------------------------------------------

    private static string BuildSession(AppServices svc, RealmType realm, DateTimeOffset now)
    {
        StringBuilder sb = new();
        Kv(sb, "Captured at", now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Kv(sb, "Realm", $"{RealmLabel(realm)} ({realm})");
        Kv(sb, "Active game-data set", svc.GameData.ActiveSet ?? "(none)");
        Kv(sb, "Character", svc.Profile.CurrentProfileName ?? "(none loaded)");
        Kv(sb, "BBS", svc.Profile.CurrentBbsName ?? "(none)");
        return sb.ToString();
    }

    private static string BuildPlayerState(AppServices svc)
    {
        StringBuilder sb = new();
        sb.Append("**Live vitals (PlayerState)**\n\n");
        sb.Append(Json(svc.PlayerState)).Append('\n');
        sb.Append("**Stat screen (PlayerStats)**\n\n");
        sb.Append(Json(svc.PlayerStats));
        return sb.ToString();
    }

    private static string BuildInventory(AppServices svc)
    {
        InventorySnapshot snapshot = svc.Inventory.Snapshot;
        return Json(snapshot);
    }

    // The Character Workshop's persisted, per-character artifacts — the gear
    // sets, the CP-allocation plan, and the quest log. These live as top-level
    // CharacterProfile properties (not in the settings-tab dictionary), so the
    // settings dump wouldn't otherwise carry them. The rest of the Workshop is
    // a read-only view over stats / inventory already captured above.
    private static string BuildWorkshop(AppServices svc)
    {
        var profile = svc.Profile.Current;
        if (profile is null) return "_(no character loaded)_";

        StringBuilder sb = new();

        sb.Append("**Gear sets (Equipment)**\n\n");
        sb.Append(profile.Equipment is { } equip ? Json(equip) : "_(none)_\n");

        var plan = profile.CharacterPlan;
        sb.Append("\n**CP allocation plan (CharacterPlan)** (").Append(plan?.Count ?? 0).Append(")\n\n");
        sb.Append(plan is { Count: > 0 } ? Json(plan) : "_(none)_\n");

        var quests = profile.QuestLog;
        sb.Append("\n**Quest log (QuestLog)** (").Append(quests?.Count ?? 0).Append(")\n\n");
        sb.Append(quests is { Count: > 0 } ? Json(quests) : "_(none)_\n");

        return sb.ToString();
    }

    private static string BuildMovement(AppServices svc)
    {
        StringBuilder sb = new();
        Kv(sb, "Coalesced state", svc.MovementControl.State.ToString());
        Kv(sb, "Active", svc.MovementControl.IsActive.ToString());
        Kv(sb, "Paused", svc.MovementControl.IsPaused.ToString());
        var loop = svc.LoopRunner;
        Kv(sb, "Loop runner", loop.State.ToString());
        // CurrentLoop is the loop of the LIVE run; StagedLoop is the loaded-but-
        // -not-started slot. They're mutually exclusive, so report both — a
        // running loop shows up under CurrentLoop, never StagedLoop.
        Kv(sb, "Running loop",
            loop.CurrentLoop is { } running
                ? $"{running.Name} — step {loop.CurrentIndex + 1}/{loop.StepCount}"
                : "(none)");
        if (loop.CurrentLoop is not null)
        {
            Kv(sb, "Loop approach target",
                loop.ApproachTarget is { } appr ? $"{appr.Map}/{appr.Room}" : "(none)");
            Kv(sb, "Loop circle start",
                loop.CircleStartRoom is { } start ? $"{start.Map}/{start.Room}" : "(none)");
        }
        Kv(sb, "Staged loop", loop.StagedLoop?.Name ?? "(none)");
        Kv(sb, "Auto-Lair phase", svc.AutoLair.Phase.ToString());
        Kv(sb, "Auto-Lair active", svc.AutoLair.IsActive.ToString());
        Kv(sb, "Auto-Lair paused", svc.AutoLair.IsPaused.ToString());
        Kv(sb, "Auto-Lair target",
            svc.AutoLair.CurrentTarget is { } lair ? $"{lair.Map}/{lair.Room}" : "(none)");

        var roomState = svc.RoomTracker.State;
        Kv(sb, "Current room",
            roomState.CurrentRoom is { } room ? $"{room.Key.Map}/{room.Key.Room} — {room.DisplayName}" : "(unknown)");
        Kv(sb, "Room confidence", roomState.Confidence.ToString());
        // RoomTracker anchors its timestamps in UTC (DateTimeOffset.UtcNow); the
        // rest of the report uses local .Now. The two are the same absolute
        // instant so all the tracker's comparisons work either way, but printing
        // the raw value would show the UTC hour next to local ones — normalize.
        Kv(sb, "Last move sent", svc.RoomTracker.LastMoveSentAt?.ToLocalTime().ToString("HH:mm:ss") ?? "(never)");

        IReadOnlyList<Game.Map.RoomKey> history = svc.RoomTracker.GetHistory();
        if (history.Count > 0)
        {
            sb.Append("\nRecent confirmed positions (newest first): ");
            sb.Append(string.Join(", ", history.Take(10).Select(k => $"{k.Map}/{k.Room}")));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string BuildRoomMarkers(AppServices svc)
    {
        StringBuilder sb = new();

        var avoided = svc.RoomBlacklist.Entries;
        sb.Append("**Avoid rooms** (").Append(avoided.Count).Append(")\n\n");
        if (avoided.Count == 0) sb.Append("_(none)_\n");
        else foreach (var r in avoided) sb.Append("- ").Append(r.Map).Append('/').Append(r.Room)
            .Append(" — ").Append(r.Name).Append('\n');

        var profile = svc.Profile.Current;
        var stash = profile?.StashRooms;
        sb.Append("\n**Stash rooms** (").Append(stash?.Count ?? 0).Append(")\n\n");
        if (stash is not { Count: > 0 }) sb.Append("_(none)_\n");
        else foreach (var r in stash) sb.Append("- ").Append(r.Map).Append('/').Append(r.Room).Append('\n');

        var favorites = svc.Favorites.All;
        sb.Append("\n**Favorites** (").Append(favorites.Count).Append(")\n\n");
        if (favorites.Count == 0) sb.Append("_(none)_\n");
        else foreach (var f in favorites)
        {
            sb.Append("- ").Append(f.Map).Append('/').Append(f.Room);
            if (!string.IsNullOrWhiteSpace(f.Label)) sb.Append(" — ").Append(f.Label);
            if (!string.IsNullOrWhiteSpace(f.Folder)) sb.Append("  (folder: ").Append(f.Folder).Append(')');
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildAutoMode(AppServices svc)
    {
        StringBuilder sb = new();
        Kv(sb, "Kill-switch engaged", svc.AutoModeController.KillSwitchEngaged.ToString());
        Kv(sb, "All wired engines off", svc.AutoModeController.AllWiredOff.ToString());
        sb.Append("\nPer-engine toggles live in the `General` settings block below (`AutoMode`).\n");
        return sb.ToString();
    }

    private static string BuildSettings(AppServices svc)
    {
        StringBuilder sb = new();
        AppendTier(sb, "Global tier", svc.Settings.Current.Settings);

        string? bbsName = svc.Profile.CurrentBbsName;
        var bbsSettings = bbsName is null ? null : svc.Bbs.Get(bbsName)?.Settings;
        AppendTier(sb, "BBS tier", bbsSettings);

        AppendTier(sb, "Character tier", svc.Profile.Current?.Settings);
        return sb.ToString();
    }

    // Emit one settings tier's deltas as JSON, dropping any BBS / Display keys
    // per the "everything except BBS + Display" scope. Those live in separate
    // stores today (BbsProfileStore / DisplayConfig), so this is belt-and-
    // braces — the tab dictionary shouldn't contain them anyway.
    private static void AppendTier(StringBuilder sb, string label, Dictionary<string, JsonElement>? tier)
    {
        sb.Append("**").Append(label).Append("**\n\n");
        if (tier is not { Count: > 0 })
        {
            sb.Append("_(no overrides)_\n\n");
            return;
        }

        Dictionary<string, JsonElement> filtered = tier
            .Where(kv => !kv.Key.Equals("Bbs", StringComparison.OrdinalIgnoreCase)
                      && !kv.Key.Equals("Display", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (filtered.Count == 0) sb.Append("_(only BBS/Display overrides — omitted)_\n\n");
        else sb.Append(Json(filtered)).Append('\n');
    }

    private static string BuildLog(AppServices svc)
    {
        LogEntry[] entries = svc.Log.Snapshot();
        int take = Math.Min(LogLines, entries.Length);
        if (take == 0) return "_(log empty)_";

        StringBuilder sb = new();
        sb.Append("Last ").Append(take).Append(" of ").Append(entries.Length).Append(" entries.\n\n```\n");
        for (int i = entries.Length - take; i < entries.Length; i++)
        {
            LogEntry e = entries[i];
            sb.Append(e.Timestamp.ToString("HH:mm:ss")).Append("  [").Append(e.Severity).Append("]  ")
              .Append(e.Source).Append(": ").Append(e.Message).Append('\n');
        }
        sb.Append("```");
        return sb.ToString();
    }

    private static string BuildScrollback(TerminalEmulator emulator)
    {
        List<string> lines = new();

        foreach (ScrollbackBuffer.Row row in emulator.Screen.Scrollback.Enumerate())
            lines.Add(RowText(row.Cells));

        TerminalScreen screen = emulator.Screen;
        for (int y = 0; y < screen.Rows; y++)
            lines.Add(RowText(screen.Row(y).ToArray()));

        // Trim only trailing blank padding rows from the live screen; keep
        // interior blanks (they may be meaningful spacing the user saw).
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        int take = Math.Min(ScrollbackLines, lines.Count);
        if (take == 0) return "_(nothing on screen yet)_";

        StringBuilder sb = new();
        sb.Append("Last ").Append(take).Append(" line(s).\n\n```\n");
        for (int i = lines.Count - take; i < lines.Count; i++) sb.Append(lines[i]).Append('\n');
        sb.Append("```");
        return sb.ToString();
    }

    // ----- Helpers -------------------------------------------------------

    private static string RowText(Cell[] cells)
    {
        StringBuilder sb = new(cells.Length);
        foreach (Cell c in cells) sb.Append(c.Char);
        // Drop trailing spaces so cell-grid padding doesn't bloat the report.
        int end = sb.Length;
        while (end > 0 && sb[end - 1] == ' ') end--;
        return sb.ToString(0, end);
    }

    private static string RealmLabel(RealmType realm) => realm switch
    {
        RealmType.ParaMud => "paradigm",
        _ => "stock",
    };

    private static void Kv(StringBuilder sb, string key, string value)
        => sb.Append("- **").Append(key).Append("**: ").Append(value).Append('\n');

    // Serialize value into a fenced JSON block.
    private static string Json(object? value)
    {
        try
        {
            return "```json\n" + JsonSerializer.Serialize(value, JsonStore.Options) + "\n```\n";
        }
        catch (Exception ex)
        {
            return $"_(could not serialize: {ex.Message})_\n";
        }
    }

    // Run a section builder, converting any throw into an inline note.
    private static string SafeSection(Func<string> build)
    {
        try { return build(); }
        catch (Exception ex) { return $"_(capture failed: {ex.Message})_"; }
    }

    private static T Guard<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
