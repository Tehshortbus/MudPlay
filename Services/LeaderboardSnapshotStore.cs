using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MudPlay.Game.Leaderboard;

namespace MudPlay.Services;

// Per-realm store of captured "top N" leaderboard snapshots. Mirrors
// RoomBlacklistStore: loads the active realm's leaderboard.json on every realm
// change, and
// exposes a read-only, newest-first history the XP/HR calculator diffs across.
//
// History is bounded (MaxSnapshots): XP/HR only reaches back to a character's
// previous changed reading, and unbounded growth on an actively-captured board
// would bloat the file for no gain. The oldest snapshots fall off the tail.
public sealed class LeaderboardSnapshotStore
{
    // A small ring is enough: the rate diffs the latest capture against the most
    // recent prior one that showed change, so a handful of captures covers every
    // listed character's baseline. Older captures give only staler baselines —
    // there's nothing to gain from hoarding them, so the oldest fall off the tail.
    private const int MaxSnapshots = 5;

    private readonly LogService? _log;
    private string? _realmFolder;
    private readonly List<LeaderboardSnapshot> _snapshots = new();

    // Fires on load against a new realm and on every capture / clear. The
    // Workshop's Realm Rankings section rebuilds its table from this.
    public event Action? Changed;

    // Raised for every listing captured from the game, kept or not: a capture that
    // adds nothing to the experience history still says who is which class.
    public event Action<LeaderboardSnapshot>? Captured;

    public LeaderboardSnapshotStore(LogService? log = null)
    {
        _log = log;
    }

    // Captured listings, newest first. Empty until the first capture on this realm.
    public IReadOnlyList<LeaderboardSnapshot> Snapshots => _snapshots;

    // Record a fresh capture at the front and trim the tail past MaxSnapshots.
    //
    // A capture only earns a slot when it differs from the most recent one in a way
    // that matters for XP/HR: someone's experience moved, or the roster changed (a
    // name dropped off and another took its place). Re-running "top N" a few times
    // on a quiet board otherwise piles up identical snapshots that can never yield a
    // rate — so an unchanged capture is discarded, and the next real change diffs
    // against the still-standing earlier one over its true elapsed time.
    public void Add(LeaderboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Captured?.Invoke(snapshot);
        TakeInOutsideChanges();
        if (_snapshots.Count > 0 && !DiffersFrom(_snapshots[0], snapshot))
        {
            _log?.Log(LogSeverity.Info, "Leaderboard",
                $"Captured {Describe(snapshot)} — no experience or roster change "
                + "since the last capture; discarded.");
            return;
        }
        _snapshots.Insert(0, snapshot);
        TrimHistory();
        Persist();
        _log?.Log(LogSeverity.Info, "Leaderboard",
            $"Captured {Describe(snapshot)} — {snapshot.Entries.Count} heroes; "
            + $"{_snapshots.Count} snapshot(s) stored.");
        LogRerollHints();
        Changed?.Invoke();
    }

    // Bound the history at MaxSnapshots, but never evict the snapshot that defines
    // the widest known board (the largest real list captured). The XP/HR table shows
    // the union of readings across the ring; if the widest capture fell off the tail,
    // a run of smaller "top 10" views would shrink the displayed board back down —
    // the very prune this store exists to prevent. So the oldest snapshot dropped is
    // always one that isn't the board-of-record.
    private void TrimHistory()
    {
        while (_snapshots.Count > MaxSnapshots)
        {
            int widest = 0;
            for (int i = 1; i < _snapshots.Count; i++)
                if (_snapshots[i].Entries.Count > _snapshots[widest].Entries.Count)
                    widest = i;

            int removeAt = _snapshots.Count - 1;
            if (removeAt == widest) removeAt--; // pin the widest; take the next-oldest
            _snapshots.RemoveAt(removeAt);
        }
    }

    // A bare "top" (no number) has no requested count; render it as "top" rather
    // than the misleading "top 0" in the log an operator reads.
    private static string Describe(LeaderboardSnapshot snapshot)
        => snapshot.RequestedCount > 0 ? $"top {snapshot.RequestedCount}" : "top";

    // After a kept capture, surface likely rerolls — a listed hero whose class
    // changed or whose experience fell versus their prior appearance — to the
    // program log. This replaces the table's old Note column: the hint now lives
    // where an operator watches. First appearances ("new") are skipped so the
    // opening capture of a full board doesn't spray a line per hero.
    private void LogRerollHints()
    {
        if (_log is null || _snapshots.Count < 2) return;
        LeaderboardReport report = LeaderboardXpRateCalculator.Build(_snapshots);
        foreach (LeaderboardRankRow row in report.Rows)
            if (row.Note.StartsWith("reroll", StringComparison.OrdinalIgnoreCase))
                _log.Log(LogSeverity.Info, "Leaderboard", $"{row.Name} ({row.Class}) — {row.Note}");
    }

    // True when candidate carries a change worth keeping vs the previous capture:
    // a different roster (identities added / removed / swapped) or any shared hero
    // whose experience moved — or whose class changed, the fingerprint of a reroll
    // reusing the name. Identity is by first name, matching the XP/HR calculator.
    private static bool DiffersFrom(LeaderboardSnapshot previous, LeaderboardSnapshot candidate)
    {
        // A first name shared by two heroes in one listing is vanishingly rare on a
        // top board; last write wins here, which is fine for a change test.
        Dictionary<string, (long Exp, string Class)> before = new(StringComparer.OrdinalIgnoreCase);
        foreach (LeaderboardEntry e in previous.Entries) before[e.FirstName] = (e.Experience, e.Class);

        Dictionary<string, (long Exp, string Class)> now = new(StringComparer.OrdinalIgnoreCase);
        foreach (LeaderboardEntry e in candidate.Entries) now[e.FirstName] = (e.Experience, e.Class);

        if (before.Count != now.Count) return true;
        foreach (KeyValuePair<string, (long Exp, string Class)> hero in now)
        {
            if (!before.TryGetValue(hero.Key, out (long Exp, string Class) was)) return true;
            if (was.Exp != hero.Value.Exp) return true;
            if (!string.Equals(was.Class, hero.Value.Class, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Wipe the history for the active realm (the tab's "Clear history" button).
    public void Clear()
    {
        TakeInOutsideChanges();
        if (_snapshots.Count == 0) return;
        _snapshots.Clear();
        Persist();
        Changed?.Invoke();
    }

    // Load the history for the active realm's folder. Wired to
    // ProfileService.ProfileLoaded / BbsPinApplied through AppServices, same as
    // RoomBlacklistStore.
    public void OnRealmChanged(string? realmFolder)
    {
        if (string.IsNullOrWhiteSpace(realmFolder))
        {
            if (_realmFolder is not null)
            {
                _realmFolder = null;
                _snapshots.Clear();
                Changed?.Invoke();
            }
            return;
        }

        _realmFolder = realmFolder;
        _snapshots.Clear();
        if (TryRead(out List<LeaderboardSnapshot> loaded))
        {
            _snapshots.AddRange(loaded);
            _log?.Log(LogSeverity.Info, "Leaderboard",
                $"Loaded {_snapshots.Count} snapshot(s) for '{realmFolder}'.");
        }

        Changed?.Invoke();
    }

    private readonly SharedFileStamp _stamp = new();

    // Re-read the history when another client on the realm has written it: every
    // character on it captures into the one file. Called on the heartbeat and before
    // a change. A file caught mid-write is left for the next call, the history kept.
    public bool TakeInOutsideChanges()
    {
        if (_realmFolder is null) return false;
        if (!_stamp.ChangedOutside(AppPaths.RealmLeaderboardFile(_realmFolder))) return false;
        if (!TryRead(out List<LeaderboardSnapshot> loaded)) return false;
        _snapshots.Clear();
        _snapshots.AddRange(loaded);
        _log?.Log(LogSeverity.Debug, "Leaderboard",
            "history re-read: another client on this realm changed it");
        Changed?.Invoke();
        return true;
    }

    // Read the realm's file. False when it is there but can't be read or parsed.
    private bool TryRead(out List<LeaderboardSnapshot> loaded)
    {
        loaded = new List<LeaderboardSnapshot>();
        string path = AppPaths.RealmLeaderboardFile(_realmFolder!);
        if (!File.Exists(path)) { _stamp.Mark(path); return true; }
        try
        {
            loaded = Usable(JsonSerializer.Deserialize<List<LeaderboardSnapshot>>(File.ReadAllText(path))
                            ?? new List<LeaderboardSnapshot>(), path);
            _stamp.Mark(path);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log?.Log(LogSeverity.Warn, "Leaderboard", $"Failed to read '{path}': {ex.Message}");
            return false;
        }
    }

    // A field a hand-edited or carried-in file leaves out reads back null, and a hero
    // is keyed by first name: a capture with no rows, or a row with no name, is dropped.
    private List<LeaderboardSnapshot> Usable(List<LeaderboardSnapshot> read, string path)
    {
        int dropped = 0;
        List<LeaderboardSnapshot> usable = new(read.Count);
        foreach (LeaderboardSnapshot? s in read)
        {
            if (s?.Entries is null) { dropped++; continue; }
            LeaderboardEntry[] rows = s.Entries.Where(e => e?.Name is not null).ToArray();
            dropped += s.Entries.Count - rows.Length;
            usable.Add(rows.Length == s.Entries.Count ? s : s with { Entries = rows });
        }
        if (dropped > 0)
            _log?.Log(LogSeverity.Warn, "Leaderboard",
                $"'{path}': left out {dropped} capture(s) or row(s) missing their heroes or a name.");
        return usable;
    }

    private void Persist()
    {
        if (_realmFolder is null) return;
        string path = AppPaths.RealmLeaderboardFile(_realmFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        // Written beside the file and moved over it, so another client reading at
        // this moment sees the old history or the new, never half of one.
        string temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_snapshots, opts));
        File.Move(temp, path, overwrite: true);
        _stamp.Mark(path);
    }
}
