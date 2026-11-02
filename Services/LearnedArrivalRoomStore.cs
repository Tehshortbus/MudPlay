using System.IO;
using System.Text.Json;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// Where the dead of a room arrive, as the client has seen it: "room died in →
// room arrived in", one reading per death it witnessed and could place at both
// ends (Game.Recovery.ArrivalRoomLearner). The game keeps this per room and the
// imported tables don't carry it, so for the rooms no list covers (Paradigm's own
// outside Albion, a board's edited ones) seeing it happen is the only way to know.
//
// It is knowledge about the game data, so it is kept with the set, in one file
// beside the imported tables, and every board and character on the set shares
// it. Two clients on one set take in each other's readings the way the realm's
// other shared files are followed (SharedFileStamp, on the heartbeat and before a
// write), and a write is made under a hold on the file (SharedFileWrite), laid over
// what is on file at that moment rather than replacing it.
//
// Each room has one assigned room for its dead, so two readings of one room mean
// one of them was misread (the owner, 2026-10-11): a display of the wrong room, a
// room the map took for its look-alike. Every reading is kept with how often it
// was seen, the one in use first.
//   - Two readings seen once each: the room is disputed and neither is used. The
//     first to be seen a second time stands, and the others are dropped.
//   - A reading seen twice or more stays in use against a different sighting,
//     which is kept beside it as a challenger with its own count. The challenger
//     takes over when its count reaches the count of the one in use, and the one
//     it replaced is kept in turn. So a stray sighting changes nothing, and a
//     reading that was wrong from the start (the same misreading twice), or a
//     graveyard the board has since moved, is corrected by seeing the right one
//     as often. Nothing is ever fixed for good, and nothing needs a hand edit.
//
// Nothing here may throw into the line that is being read: a death is learned
// from inside the room display's own handling, and a file that can't be written
// must not stop the handlers that come after.
public sealed class LearnedArrivalRoomStore
{
    public const string LogCategory = "LearnedArrivals";

    // The format this client writes. A file with a higher number is a later
    // client's: it is neither read nor written over.
    private const int FileVersion = 1;

    // A wait for another client's hold longer than this is logged.
    private static readonly TimeSpan SlowHold = TimeSpan.FromMilliseconds(50);

    private readonly LogService? _log;
    private readonly Func<string, string> _fileFor;
    private readonly SharedFileStamp _stamp = new();
    private string? _set;
    // Room died in → each room its dead were seen to arrive in, the one in use
    // first. Seen is how often in that room; Nearby how often a death in another
    // room of its land, one with no reading of its own, ended there too.
    private readonly Dictionary<RoomKey, List<(RoomKey Arrival, int Seen, int Nearby)>> _seen = new();
    // Sightings made since the last write that reached the disk, replayed over
    // whatever another client has written since.
    private readonly List<(RoomKey DiedIn, RoomKey Arrival, bool Nearby)> _unwritten = new();
    // How the file on disk stands. Each is said once, when it becomes so.
    //   _unparsed   — it was read and doesn't parse: set aside before anything is
    //                 written.
    //   _unread     — it couldn't be opened: nothing is known of what it holds, so
    //                 nothing is written until it has been read.
    //   _newer      — a later client's format: neither read nor written.
    //   _unwritable — the last write failed.
    private bool _unparsed, _unread, _newer, _unwritable;

    //   fileFor — a set's file; AppPaths.LearnedArrivalRoomsFile unless a test
    //             keeps its sets somewhere else.
    public LearnedArrivalRoomStore(LogService? log = null, Func<string, string>? fileFor = null)
    {
        _log = log;
        _fileFor = fileFor ?? AppPaths.LearnedArrivalRoomsFile;
    }

    // Goes up whenever what is held changes, for a reader that keeps an answer
    // worked out from it (Game.Recovery.LearnedArrivalLookup).
    public int Revision { get; private set; }

    // The rooms with a reading in use.
    public int Count => _seen.Values.Count(r => !IsDisputed(r));

    public int DisputedCount => _seen.Values.Count(IsDisputed);

    // Every room with a reading in use, and the room its dead arrive in.
    public IReadOnlyList<(RoomKey DiedIn, RoomKey Arrival)> Known =>
        _seen.Where(e => !IsDisputed(e.Value))
            .OrderBy(e => e.Key.Map).ThenBy(e => e.Key.Room)
            .Select(e => (e.Key, e.Value[0].Arrival))
            .ToList();

    // The disputed rooms with every reading of them, for the bug report.
    public IReadOnlyList<(RoomKey DiedIn, IReadOnlyList<RoomKey> Arrivals)> Disputed =>
        _seen.Where(e => IsDisputed(e.Value))
            .OrderBy(e => e.Key.Map).ThenBy(e => e.Key.Room)
            .Select(e => (e.Key, (IReadOnlyList<RoomKey>)e.Value.Select(r => r.Arrival).ToList()))
            .ToList();

    // The rooms whose reading in use has a challenger, as "room → arrival (times
    // seen), against arrival (times seen)", for the bug report.
    public IReadOnlyList<string> Challenged =>
        _seen.Where(e => !IsDisputed(e.Value) && e.Value.Count > 1)
            .OrderBy(e => e.Key.Map).ThenBy(e => e.Key.Room)
            .Select(e => $"{e.Key} → {e.Value[0].Arrival} ({e.Value[0].Seen}), against "
                + string.Join(", ", e.Value.Skip(1).Select(r => $"{r.Arrival} ({r.Seen})")))
            .ToList();

    // The rooms the dead of this room were seen to arrive in, or null when none
    // was seen or the readings disagree.
    //
    // A room that names no graveyard sends its dead to one of the engine's two
    // default rooms, by the character's evil points (GAME_MECHANICS "Death
    // threshold & consequences"). So a death seen to arrive in either stands for
    // both: the two are kept as one reading.
    public IReadOnlyList<RoomKey>? ArrivalsFor(RoomKey diedIn)
    {
        if (!_seen.TryGetValue(diedIn, out List<(RoomKey Arrival, int Seen, int Nearby)>? readings) || IsDisputed(readings))
            return null;
        return readings[0].Arrival == DefaultArrivals[0] ? DefaultArrivals : new[] { readings[0].Arrival };
    }

    // Whether any death has been seen in this room, in use or disputed.
    public bool HasReadingOf(RoomKey diedIn) => _seen.ContainsKey(diedIn);

    // How often a death in another room of this room's land, one with no reading
    // of its own, ended where this room's dead arrive (LearnNearby).
    public int SeenNearby(RoomKey diedIn) =>
        _seen.TryGetValue(diedIn, out List<(RoomKey Arrival, int Seen, int Nearby)>? readings) && !IsDisputed(readings)
            ? readings[0].Nearby
            : 0;

    // Every reading in use with its nearby count, in no order: for a reader that
    // goes through all of them and has no use for Known's sorting.
    public IEnumerable<(RoomKey DiedIn, RoomKey Arrival, int Nearby)> InUse
    {
        get
        {
            foreach ((RoomKey diedIn, List<(RoomKey Arrival, int Seen, int Nearby)> readings) in _seen)
                if (!IsDisputed(readings)) yield return (diedIn, readings[0].Arrival, readings[0].Nearby);
        }
    }

    private static readonly RoomKey[] DefaultArrivals =
    {
        Game.Recovery.HangupDeathArrivalRooms.Temple, Game.Recovery.HangupDeathArrivalRooms.EvilTomb,
    };

    // The Earthen Tomb is filed as the Temple: one reading for the pair.
    private static RoomKey Filed(RoomKey arrival) => arrival == DefaultArrivals[1] ? DefaultArrivals[0] : arrival;

    // Several readings, and the first has not been seen twice: none is in use.
    private static bool IsDisputed(List<(RoomKey Arrival, int Seen, int Nearby)> readings) =>
        readings.Count > 1 && readings[0].Seen < 2;

    // GameDataCache.ActiveSetChanged: the readings are the set's.
    public void OnActiveSetChanged(string? setName)
    {
        TryWrite();
        _set = string.IsNullOrWhiteSpace(setName) ? null : setName;
        _seen.Clear();
        _unwritten.Clear();
        _unparsed = _unread = _newer = _unwritable = false;
        Revision++;
        if (_set is null) return;
        foreach (string temp in SharedFileWrite.RemoveStaleTemps(_fileFor(_set)))
            _log?.Info(LogCategory, $"removed '{temp}', left by a write that never finished");
        if (TryRead(out Dictionary<RoomKey, List<(RoomKey, int, int)>> onFile)) Take(onFile);
        if (_seen.Count > 0)
            _log?.Info(LogCategory,
                $"{Count} learned arrival room(s) for '{_set}'{(DisputedCount > 0 ? $", and {DisputedCount} disputed" : "")}.");
    }

    // Re-read the file when another client on the set has written it, and write
    // what an earlier write couldn't. Called on the heartbeat and before a
    // sighting is added. A file that couldn't be opened last time is tried again
    // whether or not it has changed; one that can't be read is left for the next
    // call, what is held kept. True when what is held changed.
    public bool TakeInOutsideChanges()
    {
        if (_set is null) return false;
        bool changed = false;
        if ((_unread || _stamp.ChangedOutside(_fileFor(_set)))
            && TryRead(out Dictionary<RoomKey, List<(RoomKey, int, int)>> onFile))
        {
            TakeWithUnwritten(onFile);
            _log?.Debug(LogCategory, "re-read: another client on this set learned something, or the file opened again");
            changed = true;
        }
        TryWrite();
        return changed;
    }

    // A death seen in diedIn put the character in arrivedIn.
    public LearnedArrivalOutcome Learn(RoomKey diedIn, RoomKey arrivedIn)
    {
        if (_set is null) return LearnedArrivalOutcome.NotKept;
        TakeInOutsideChanges();
        LearnedArrivalOutcome outcome = Add(diedIn, arrivedIn, quiet: false);
        _unwritten.Add((diedIn, arrivedIn, false));
        TryWrite();
        return outcome;
    }

    // A death seen in diedIn, a room with no reading of its own, ended where the
    // dead of anchor arrive, anchor being the room of its land whose reading said
    // so. It is counted there and no entry is made for diedIn: the land's reading
    // already speaks for it, and an entry for every room died in would make every
    // later question of the land read them all. False when anchor no longer holds
    // that reading (another client's write changed it): nothing was counted.
    public bool LearnNearby(RoomKey anchor, RoomKey diedIn, RoomKey arrivedIn)
    {
        if (_set is null) return false;
        TakeInOutsideChanges();
        if (!AddNearby(anchor, arrivedIn)) return false;
        _log?.Info(LogCategory,
            $"as its land has it: the dead of {diedIn} arrive in {arrivedIn}, like those of {anchor}. Counted there "
            + $"({SeenNearby(anchor)} such); no entry of its own");
        _unwritten.Add((anchor, arrivedIn, true));
        TryWrite();
        return true;
    }

    private bool AddNearby(RoomKey anchor, RoomKey arrivedIn)
    {
        if (!_seen.TryGetValue(anchor, out List<(RoomKey Arrival, int Seen, int Nearby)>? readings)
            || IsDisputed(readings) || readings[0].Arrival != Filed(arrivedIn))
            return false;
        readings[0] = readings[0] with { Nearby = readings[0].Nearby + 1 };
        Revision++;
        return true;
    }

    //   quiet — a sighting replayed over a re-read file: it was said when it was made.
    private LearnedArrivalOutcome Add(RoomKey diedIn, RoomKey arrivedIn, bool quiet)
    {
        Revision++;
        RoomKey filed = Filed(arrivedIn);
        if (!_seen.TryGetValue(diedIn, out List<(RoomKey Arrival, int Seen, int Nearby)>? readings))
        {
            _seen[diedIn] = new List<(RoomKey, int, int)> { (filed, 1, 0) };
            if (!quiet) _log?.Info(LogCategory, $"learned: the dead of {diedIn} arrive in {arrivedIn}");
            return LearnedArrivalOutcome.Learned;
        }

        bool wasDisputed = IsDisputed(readings);
        (RoomKey Arrival, int Seen, int Nearby) inUse = readings[0];
        int at = readings.FindIndex(r => r.Arrival == filed);
        if (at < 0)
        {
            readings.Add((filed, 1, 0));
            if (readings.Count == 2 && inUse.Seen < 2)
            {
                if (!quiet)
                    _log?.Warn(LogCategory,
                        $"The dead of {diedIn} were seen to arrive in {inUse.Arrival} before and in {arrivedIn} now. A room "
                        + "has one assigned room for its dead, so one of the two was misread. Both are kept and neither is "
                        + "used until one is seen a second time; that one then stands and the other is dropped.");
                return LearnedArrivalOutcome.Disputed;
            }
            if (wasDisputed) return LearnedArrivalOutcome.Disputed;
            if (!quiet)
                _log?.Warn(LogCategory,
                    $"A death seen in {diedIn} ended in {arrivedIn}, where its dead have been seen to arrive in "
                    + $"{inUse.Arrival} {inUse.Seen} times. {inUse.Arrival} stays in use; {arrivedIn} is kept beside it, and "
                    + "takes over if it comes to be seen as often.");
            return LearnedArrivalOutcome.Challenged;
        }

        readings[at] = readings[at] with { Seen = readings[at].Seen + 1 };
        if (at == 0 && !wasDisputed) return LearnedArrivalOutcome.Known;
        if (wasDisputed)
        {
            // Seen a second time while none was in use: it stands, the rest go.
            string dropped = string.Join(", ", readings.Where(r => r.Arrival != filed).Select(r => r.Arrival.ToString()));
            _seen[diedIn] = new List<(RoomKey, int, int)> { readings[at] };
            if (!quiet)
                _log?.Info(LogCategory,
                    $"settled: the dead of {diedIn} arrive in {arrivedIn}, seen a second time; the other reading ({dropped}) "
                    + "is dropped as a misreading");
            return LearnedArrivalOutcome.Settled;
        }

        (RoomKey Arrival, int Seen, int Nearby) challenger = readings[at];
        if (challenger.Seen < inUse.Seen)
        {
            if (!quiet)
                _log?.Info(LogCategory,
                    $"seen again: a death in {diedIn} ended in {arrivedIn} ({challenger.Seen} times now), against "
                    + $"{inUse.Arrival} in use ({inUse.Seen} times). It takes over at {inUse.Seen}");
            return LearnedArrivalOutcome.Challenged;
        }
        // As often seen as the one in use: the later evidence stands, and the
        // reading it replaces is kept as its challenger.
        readings.RemoveAt(at);
        readings.Insert(0, challenger);
        if (!quiet)
            _log?.Info(LogCategory,
                $"changed: the dead of {diedIn} arrive in {arrivedIn}, now seen {challenger.Seen} times, as often as "
                + $"{inUse.Arrival} ({inUse.Seen} times), which was in use until now and is kept beside it");
        return LearnedArrivalOutcome.TookOver;
    }

    private void Take(Dictionary<RoomKey, List<(RoomKey, int, int)>> onFile)
    {
        foreach ((RoomKey diedIn, List<(RoomKey, int, int)> readings) in onFile) _seen[diedIn] = readings;
        Revision++;
    }

    // Hold what is on file, with what this client saw and hasn't written laid
    // over it: that is not in the file.
    private void TakeWithUnwritten(Dictionary<RoomKey, List<(RoomKey, int, int)>> onFile)
    {
        _seen.Clear();
        Take(onFile);
        foreach ((RoomKey diedIn, RoomKey arrival, bool nearby) in _unwritten)
        {
            if (nearby) AddNearby(diedIn, arrival);
            else Add(diedIn, arrival, quiet: true);
        }
    }

    // ----- the file ------------------------------------------------------

    private sealed class FileShape
    {
        public int Version { get; set; }
        public List<RoomShape> Rooms { get; set; } = new();
    }

    // One room of the file: where a death was seen, and each room it was seen to
    // lead to with how often, rooms as "map/room". The reading in use is first.
    private sealed class RoomShape
    {
        public string DiedIn { get; set; } = "";
        public List<SightingShape> Seen { get; set; } = new();
    }

    private sealed class SightingShape
    {
        public string Arrival { get; set; } = "";
        public int Times { get; set; } = 1;
        // Deaths in other rooms of its land that ended here too (LearnNearby).
        public int Nearby { get; set; }
    }

    // Read the set's file. False when it is there but couldn't be opened,
    // doesn't parse, or is a later client's.
    //   - One that doesn't parse is damaged: the stamp is marked, so it is looked
    //     at, and said, once, and it is set aside before the next write.
    //   - One that couldn't be opened (another program holds it, no permission)
    //     may be whole. Nothing is known of it: it is tried again on every
    //     heartbeat, and nothing is written over it meanwhile.
    private bool TryRead(out Dictionary<RoomKey, List<(RoomKey, int, int)>> onFile)
    {
        onFile = new Dictionary<RoomKey, List<(RoomKey, int, int)>>();
        string path = _fileFor(_set!);
        try
        {
            if (!File.Exists(path))
            {
                _stamp.Mark(path);
                _unparsed = _newer = false;
                Opened(path);
                return true;
            }
            string text = File.ReadAllText(path);
            _stamp.Mark(path);
            Opened(path);
            FileShape? file = JsonSerializer.Deserialize<FileShape>(text);
            if (file is null || file.Version < 1) throw new JsonException("no version");
            if (file.Version > FileVersion)
            {
                if (!_newer)
                    _log?.Warn(LogCategory,
                        $"'{path}' was written by a later version of the client (format {file.Version}). It is left alone: "
                        + "nothing is read from it, and what is learned now is kept for this session only.");
                _newer = true;
                return false;
            }
            // A hand-edited file can hold a null where a list or an entry goes; it is
            // passed over like an entry naming no room.
            foreach (RoomShape? room in file.Rooms ?? [])
            {
                if (room?.Seen is null || !RoomKey.TryParseWire(room.DiedIn, out RoomKey diedIn)) continue;
                List<(RoomKey Arrival, int Seen, int Nearby)> readings = new();
                foreach (SightingShape? sighting in room.Seen)
                {
                    if (sighting is null || !RoomKey.TryParseWire(sighting.Arrival, out RoomKey arrival)) continue;
                    arrival = Filed(arrival);
                    int already = readings.FindIndex(r => r.Arrival == arrival);
                    int times = Math.Max(1, sighting.Times), nearby = Math.Max(0, sighting.Nearby);
                    if (already >= 0)
                        readings[already] = (arrival, readings[already].Seen + times, readings[already].Nearby + nearby);
                    else readings.Add((arrival, times, nearby));
                }
                if (readings.Count == 0) continue;
                // The one in use is first in a file this client wrote. In one put
                // together by hand, the one seen most is taken for it.
                int most = readings.Max(r => r.Seen);
                if (readings[0].Seen < most)
                {
                    int lead = readings.FindIndex(r => r.Seen == most);
                    (RoomKey, int, int) first = readings[lead];
                    readings.RemoveAt(lead);
                    readings.Insert(0, first);
                }
                onFile[diedIn] = readings;
            }
            _unparsed = _newer = false;
            return true;
        }
        catch (JsonException ex)
        {
            if (!_unparsed)
                _log?.Warn(LogCategory,
                    $"'{path}' can't be read ({ex.Message}). It is left as it is; if something is learned before it reads "
                    + "again, it is set aside under another name first, never written over.");
            _unparsed = true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_unread)
                _log?.Warn(LogCategory,
                    $"'{path}' can't be opened ({ex.Message}). It is tried again each second, and nothing is written to "
                    + "it until it has been read: what is learned meanwhile is held and written then.");
            _unread = true;
            return false;
        }
    }

    private void Opened(string path)
    {
        if (!_unread) return;
        _unread = false;
        _log?.Info(LogCategory, $"'{path}' could be opened again.");
    }

    // Write what is held, when there is something the disk doesn't have. A failure
    // is said once and tried again on the next sighting or heartbeat; what was
    // seen stays in memory meanwhile.
    //
    // The file is read again and written under one hold, so no other client
    // writes in between: what this client saw goes over everything on file, a
    // write made within the file clock's step of this client's last read too,
    // which the stamp can't tell from that read.
    private void TryWrite()
    {
        if (_set is null || _unwritten.Count == 0 || _newer || _unread) return;
        string path = _fileFor(_set);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using IDisposable hold = SharedFileWrite.Hold(path, out TimeSpan waited);
            if (waited > SlowHold)
                _log?.Debug(LogCategory, $"waited {waited.TotalMilliseconds:0} ms for another client's write of '{path}'");
            if (TryRead(out Dictionary<RoomKey, List<(RoomKey, int, int)>> onFile)) TakeWithUnwritten(onFile);
            else if (_newer || _unread) return;
            // A file that doesn't parse may hold everything learned so far, one
            // slip of a hand edit away from reading again. It is moved aside, under
            // a name nothing else has, before anything is written in its place.
            if (_unparsed && File.Exists(path))
            {
                string aside = path + ".bad";
                if (File.Exists(aside)) aside = $"{path}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.bad";
                File.Move(path, aside);
                _log?.Warn(LogCategory,
                    $"'{path}' couldn't be read, so it was set aside as '{aside}' before what was learned now was written. "
                    + "Nothing in it was lost: mend it and put it back to have its rooms again.");
            }
            _unparsed = false;

            FileShape file = new()
            {
                Version = FileVersion,
                Rooms = _seen
                    .OrderBy(e => e.Key.Map).ThenBy(e => e.Key.Room)
                    .Select(e => new RoomShape
                    {
                        DiedIn = e.Key.ToString(),
                        Seen = e.Value
                            .Select(r => new SightingShape { Arrival = r.Arrival.ToString(), Times = r.Seen, Nearby = r.Nearby })
                            .ToList(),
                    })
                    .ToList(),
            };
            // Written beside the file and moved over it, so another client reading
            // at this moment sees the old list or the new, never half of one.
            string temp = SharedFileWrite.TempFor(path);
            File.WriteAllText(temp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            _stamp.Mark(path);
            _unwritten.Clear();
            if (_unwritable)
            {
                _unwritable = false;
                _log?.Info(LogCategory, $"'{path}' could be written again: what was learned meanwhile is on file.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Said once, whatever else is wrong with the file: a set-aside that
            // fails is a write that failed.
            if (!_unwritable)
                _log?.Warn(LogCategory,
                    $"'{path}' can't be written ({ex.Message}). What was learned is kept for this session and written when "
                    + "the file can be.");
            _unwritable = true;
        }
    }
}
