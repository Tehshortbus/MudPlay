using MudPlay.Services;

namespace MudPlay.Game.Simulation;

// Which game data this client is running on, for a ranking kept between sessions
// (RankingWorld): the table files as they were when the client read them, not as
// they are on disk now. Another client can import the set while this one runs; this
// one goes on with the rows it read, a ranking it makes is a ranking of those, and
// stamped with the new files it would be trusted after a restart.
//
// The cache says which file it last read each table from in this load of the set
// (stampAsRead), and says nothing (null) for three different things: a table it has
// not read in this load, one whose file was gone at its latest read, and one whose
// latest read failed. They are told apart here.
//
// The cache also drops a table nobody has read for a while and reads it again when
// asked, and then knows only the later file. Had that file changed in between, the
// room map and catalogs built at load are from one version and the rows looked up
// since from another. So the first stamp seen for a table in a load is remembered
// here, and the load is unfit, with no world given and so no ranking saved or read
// back until the set is read in again (a restart, or an import in this client),
// once a table
//   - is read again from a file that is not the one first read (changed), or
//   - was read and the cache no longer has a stamp for it: its latest read failed,
//     or its file is gone (lost).
// Observe is called on the heartbeat, far more often than the cache can drop a table
// and read it again, so the first read is the one remembered.
//
// A table the cache has not read in this load is treated two ways.
//
// To compare a saved ranking with (Now): it is stamped with its file as it is on
// disk (stampOnDisk; null with no file), and held to that for the rest of the load.
// Nothing in memory came from an older file, and that file is what any lookup from
// here on reads; read later from another, it counts as changed.
//
// To stamp a ranking just made (NowForSaving): it is read first (read). A stamp
// taken from disk would vouch for a file this client never managed to read, so one
// that still has no stamp after that, with a file there, could not be read, and the
// ranking is not saved. Only a set without the table (Lairs is optional in an
// export) is stamped as without it.
public sealed class RankingWorldWatch(
    Func<string, string?> stampAsRead, Func<string, string?> stampOnDisk, Action<string>? read = null)
{
    // The stamp of a table the set does not have.
    public const string NoTable = "none";

    // A table file's stamp: its length and last-write time. The tables run to tens
    // of megabytes and an import rewrites them, so the pair tells two apart without
    // reading either. Both sources hand theirs in through this, so a table stamped
    // from disk and then read from that same file is one stamp.
    public static string Stamp(long length, DateTime lastWriteUtc) => $"{length}:{lastWriteUtc.Ticks}";

    // The watch over the app's own cache: what the cache read each table from, the
    // table's file under the active set's folder, and a lookup to read one with.
    // The load to hand Observe and the two Nows is the cache's ActiveLoad.
    public static RankingWorldWatch Over(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return new RankingWorldWatch(
            table => cache.FileStampAsRead(table) is { } stamp ? Stamp(stamp.Length, stamp.LastWriteUtc) : null,
            table => cache.ActiveSet is { } set
                ? RankingStampReader.TableOnDisk(Path.Combine(cache.GameDataRoot, set), table)
                : null,
            table => cache.GetTable(table));
    }

    private readonly object _seenLock = new();
    private readonly Dictionary<string, string> _first = new(StringComparer.Ordinal);
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _changed = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _lost = new(StringComparer.Ordinal);
    private object? _load;

    // Why the load in hand has no one world, in words for a log or a report; null
    // when it has.
    public string? WhyUnfit
    {
        get { lock (_seenLock) return Unfit(); }
    }

    // Take in what the cache has read by now. load says which load of which set is
    // in hand: two that are equal are the same load, and anything remembered of
    // another is forgotten.
    public void Observe(object? load)
    {
        lock (_seenLock) See(load);
    }

    // The world to compare a saved ranking with, or null when the load has no one
    // world, with why in words for the program log.
    public RankingWorld? Now(object? load, string seed, out string? why)
    {
        ArgumentNullException.ThrowIfNull(seed);
        lock (_seenLock)
        {
            See(load);
            if ((why = Unfit()) is not null) return null;
            foreach (string table in RankingWorld.TableNames)
                if (!_first.ContainsKey(table)) _first[table] = stampOnDisk(table) ?? NoTable;
            return World(seed);
        }
    }

    // The world a ranking just made in this load was made in, or null, with why,
    // when the load has no one world or a table has a file and could not be read.
    public RankingWorld? NowForSaving(object? load, string seed, out string? why)
    {
        ArgumentNullException.ThrowIfNull(seed);
        // Outside the lock: a read is a parse, and the cache takes its own.
        foreach (string table in RankingWorld.TableNames)
        {
            if (stampAsRead(table) is not null || stampOnDisk(table) is null) continue;
            // Whatever goes wrong is told just below, by the table still having no
            // stamp: which kind of failure it was is the cache's to log.
            try { read?.Invoke(table); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        lock (_seenLock)
        {
            See(load);
            if ((why = Unfit()) is not null) return null;
            List<string> unread = RankingWorld.TableNames
                .Where(t => stampAsRead(t) is null && stampOnDisk(t) is not null).ToList();
            if (unread.Count > 0)
            {
                why = $"{string.Join(", ", unread)} could not be read";
                return null;
            }
            foreach (string table in RankingWorld.TableNames)
                if (stampAsRead(table) is null) _first[table] = NoTable;
            return World(seed);
        }
    }

    private RankingWorld World(string seed) => new(LoopSimulator.SimulatorVersion,
        RankingWorld.TableNames.ToDictionary(t => t, t => _first[t], StringComparer.Ordinal), seed);

    private string? Unfit()
    {
        var parts = new List<string>();
        if (_changed.Count > 0)
            parts.Add($"{string.Join(", ", _changed)} {(_changed.Count == 1 ? "was" : "were")} read again from a file " +
                      "that had changed since the set was loaded");
        if (_lost.Count > 0)
            parts.Add($"{string.Join(", ", _lost)} could not be read again (the file is gone or would not read)");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private void See(object? load)
    {
        if (!Equals(load, _load))
        {
            _load = load;
            _first.Clear();
            _read.Clear();
            _changed.Clear();
            _lost.Clear();
        }
        foreach (string table in RankingWorld.TableNames)
        {
            if (stampAsRead(table) is not { } stamp)
            {
                if (_read.Contains(table)) _lost.Add(table);
                continue;
            }
            _read.Add(table);
            if (!_first.TryGetValue(table, out string? first)) _first[table] = stamp;
            else if (!string.Equals(first, stamp, StringComparison.Ordinal)) _changed.Add(table);
        }
    }
}
