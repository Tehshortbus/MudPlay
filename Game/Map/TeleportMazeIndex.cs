using System.Collections.Generic;
using System.Text;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Per-active-set index of "teleport-maze" pockets — random-teleport areas
// (the Warped Asylum is the canonical one) where the tracker can't tell which
// room it's in because every room shares a name and a plain-exit fingerprint
// with its siblings, so normal position tracking collapses to Lost and the
// walker can't source a route.
//
// A maze pocket is detected structurally, with no hardcoded room numbers or
// names: it is the set of rooms trapped behind a one-way cast-pocket entrance
// (RoomExit.CastPocketEntrance — a cast-on-walk mouth you can't walk back out
// of) whose interior contains at least one random-teleport exit
// (RoomExit.CastTeleportRandom). That combination is exactly a random-teleport
// maze and nothing else.
//
// Relocalization key — the "1x2 signature": a room's own obvious-exits mask
// plus, for each of those exits, the neighbour room's obvious-exits mask (read
// live via `look <dir>`, which peeks a neighbour without moving). Within a
// pocket the teleport only ever drops you into a corridor room, and every
// corridor room's 1x2 signature is unique — so the signature identifies exactly
// where a teleport landed even though the room name and solo fingerprint don't.
// Signatures that aren't unique within a pocket (dead-end cells you only ever
// reach deterministically by walking, so never need to relocalize from) are
// omitted from the lookup rather than risk a mis-identification.
public sealed class TeleportMazeIndex
{
    // Guard against a pathological "pocket" swallowing a huge chunk of the map
    // (mis-flagged one-way topology). Real random-teleport pockets are small.
    private const int MaxPocketRooms = 4000;

    private readonly RoomGraphManager _graph;
    private readonly LogService? _log;

    private readonly HashSet<RoomKey> _mazeRooms = new();
    private readonly Dictionary<string, RoomKey> _bySignature = new();
    private readonly HashSet<RoomKey> _relocatable = new();
    private readonly Dictionary<RoomKey, IReadOnlyList<Direction>> _reshuffleDirs = new();

    // Every pocket member → the one-way cast mouth that enters its pocket (the
    // outside room holding the CastPocketEntrance exit, and the direction of
    // that exit). Lets a walker standing outside route to the mouth and cross in.
    private readonly Dictionary<RoomKey, (RoomKey Source, Direction Dir)> _entranceByRoom = new();

    public TeleportMazeIndex(RoomGraphManager graph, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _log = log;
        _graph.GraphReloaded += Rebuild;
        Rebuild();
    }

    // Any teleport-maze pocket present in the active set.
    public bool HasMazes => _mazeRooms.Count > 0;

    // True when key is a member of a teleport-maze pocket — the signal the
    // walker uses to decide a destination needs the maze solver rather than
    // normal routing.
    public bool IsMazeRoom(RoomKey key) => _mazeRooms.Contains(key);

    // Directions out of key whose walk fires a random teleport — the moves the
    // solver uses to "reshuffle" when it lands somewhere with no plain route to
    // the goal. Empty for a room with none (or a non-maze room).
    public IReadOnlyList<Direction> ReshuffleDirections(RoomKey key)
        => _reshuffleDirs.TryGetValue(key, out IReadOnlyList<Direction>? dirs)
            ? dirs
            : Array.Empty<Direction>();

    // True when key has a 1x2 signature unique within its pocket, so a live look
    // sweep can relocalize onto it after a teleport. False for the dead-end cells
    // whose signature collides with a sibling (the index omits them). The
    // reshuffle-spell chooser uses this to value a teleport pool by how many of
    // its landings the solver could actually identify and route from.
    public bool IsUniquelyRelocatable(RoomKey key) => _relocatable.Contains(key);

    // The one-way cast mouth that leads into the pocket containing mazeRoom — the
    // room a walker must stand in (source) and the direction it walks (direction)
    // to cross into the maze. Crossing the mouth fires the random teleport that
    // drops the player somewhere in the pocket. False for a non-maze room, or a
    // pocket whose entrance wasn't captured.
    public bool TryGetEntrance(RoomKey mazeRoom, out RoomKey source, out Direction direction)
    {
        if (_entranceByRoom.TryGetValue(mazeRoom, out (RoomKey Source, Direction Dir) e))
        {
            source = e.Source;
            direction = e.Dir;
            return true;
        }
        source = default;
        direction = default;
        return false;
    }

    // Resolve the room the player is standing in from its live 1x2 signature:
    // ownMask is the current room's obvious-exits mask; neighbourMasks maps each
    // of those exit directions to the mask revealed by `look <dir>`. Returns
    // false when the signature is incomplete (a look didn't resolve) or not a
    // unique maze room.
    public bool TryIdentify(uint ownMask,
        IReadOnlyDictionary<Direction, uint> neighbourMasks,
        out RoomKey key)
    {
        key = default;
        ArgumentNullException.ThrowIfNull(neighbourMasks);
        string? sig = BuildSignature(ownMask,
            dir => neighbourMasks.TryGetValue(dir, out uint m) ? m : null);
        return sig is not null && _bySignature.TryGetValue(sig, out key);
    }

    private void Rebuild()
    {
        _mazeRooms.Clear();
        _bySignature.Clear();
        _relocatable.Clear();
        _reshuffleDirs.Clear();
        _entranceByRoom.Clear();

        var processed = new HashSet<RoomKey>();          // pocket rooms already indexed
        var sigBuckets = new Dictionary<string, List<RoomKey>>();

        foreach (Room room in _graph.Rooms)
        {
            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                if (!exit.CastPocketEntrance) continue;
                if (processed.Contains(exit.Target)) continue;   // pocket seen already

                HashSet<RoomKey>? pocket = CollectPocket(exit.Target);
                if (pocket is null) continue;                    // too big — not a real pocket
                foreach (RoomKey k in pocket) processed.Add(k);

                if (!PocketHasRandomTeleport(pocket)) continue;  // deterministic pocket — BFS handles it

                IndexPocket(pocket, sigBuckets);
                foreach (RoomKey k in pocket) _entranceByRoom[k] = (room.Key, dir);
            }
        }

        // Keep only signatures unique within the indexed pockets — a colliding
        // signature can't identify a landing, so leave it out rather than pick
        // wrong.
        foreach ((string sig, List<RoomKey> keys) in sigBuckets)
            if (keys.Count == 1)
            {
                _bySignature[sig] = keys[0];
                _relocatable.Add(keys[0]);
            }

        if (_mazeRooms.Count > 0)
            _log?.Log(LogSeverity.Info, "TeleportMaze",
                $"indexed {_mazeRooms.Count} maze room(s) across teleport pocket(s); "
                + $"{_bySignature.Count} uniquely relocatable by 1x2 signature.");
    }

    // Rooms reachable from seed over all exits. Bounded — a genuine one-way
    // pocket is small; returns null if it blows past the cap (mis-flagged
    // topology that would otherwise swallow the overworld).
    private HashSet<RoomKey>? CollectPocket(RoomKey seed)
    {
        var seen = new HashSet<RoomKey> { seed };
        var queue = new Queue<RoomKey>();
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            RoomKey cur = queue.Dequeue();
            if (_graph.GetRoom(cur) is not { } room) continue;
            foreach (RoomExit exit in room.Exits.Values)
            {
                if (seen.Add(exit.Target))
                {
                    if (seen.Count > MaxPocketRooms) return null;
                    queue.Enqueue(exit.Target);
                }
            }
        }
        return seen;
    }

    private bool PocketHasRandomTeleport(HashSet<RoomKey> pocket)
    {
        foreach (RoomKey k in pocket)
            if (_graph.GetRoom(k) is { } room)
                foreach (RoomExit exit in room.Exits.Values)
                    if (exit.CastTeleportRandom)
                        return true;
        return false;
    }

    private void IndexPocket(HashSet<RoomKey> pocket,
        Dictionary<string, List<RoomKey>> sigBuckets)
    {
        foreach (RoomKey k in pocket)
        {
            if (_graph.GetRoom(k) is not { } room) continue;
            _mazeRooms.Add(k);

            List<Direction>? reshuffle = null;
            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                if (exit.CastTeleportRandom)
                    (reshuffle ??= new List<Direction>()).Add(dir);
            }
            if (reshuffle is not null) _reshuffleDirs[k] = reshuffle;

            string? sig = BuildSignature(room.ExitMask,
                dir => NeighbourMask(room, dir));
            if (sig is null) continue;
            if (!sigBuckets.TryGetValue(sig, out List<RoomKey>? bucket))
                sigBuckets[sig] = bucket = new List<RoomKey>();
            bucket.Add(k);
        }
    }

    // The obvious-exits mask of the room reached by stepping dir out of room —
    // what `look <dir>` reveals. Null when that direction isn't an exit or its
    // target isn't in the graph (signature can't be built).
    private uint? NeighbourMask(Room room, Direction dir)
    {
        if (!room.Exits.TryGetValue(dir, out RoomExit exit)) return null;
        return _graph.GetRoom(exit.Target) is { } neighbour ? neighbour.ExitMask : null;
    }

    // Canonical 1x2 signature string: own mask, then each cardinal exit
    // direction present in ownMask (enum order) with its neighbour's mask. The
    // index-build and the live-look paths call this identically so the strings
    // match. Returns null if any required neighbour mask is unavailable.
    private static string? BuildSignature(uint ownMask, Func<Direction, uint?> neighbourMaskFor)
    {
        var sb = new StringBuilder();
        sb.Append(ownMask);
        // Cardinal directions only (N..D); Teleport is not part of ExitMask and
        // isn't revealed by a `look`.
        for (int d = (int)Direction.N; d <= (int)Direction.D; d++)
        {
            if ((ownMask & (1u << d)) == 0) continue;
            uint? nm = neighbourMaskFor((Direction)d);
            if (nm is null) return null;
            sb.Append('|').Append(d).Append(':').Append(nm.Value);
        }
        return sb.ToString();
    }
}
