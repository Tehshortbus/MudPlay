namespace MudPlay.Game.Map;

// The floors reachable through up/down exits from a planar layout, placed on that
// layout's grid so the map can show them dimmed beside the current floor: a
// mountain path that climbs and drops reads as one path instead of a redraw per
// level.
//
// A floor reached by an up/down exit is shifted so the room the exit leads to sits
// on the cell of the room it leaves: straight above or below it, as the game has
// it.
//
// Which exit a floor hangs from matters, because the game's geography doesn't add
// up: two chains of stairs between the same pair of floors rarely agree on where
// one sits over the other. Every floor walked across on the way adds its own
// bends, so a floor is hung from the chain that crosses the least ground on the
// floors between: a shaft of single rooms costs nothing however deep it goes, a
// route through a reef and an underground lake costs their width. The chain with
// the fewest floors used to win, and put the old world on top of the new lands by
// way of the lagoon instead of beside them by way of the Frozen Cavern's shaft
// (user, 2026-10-03). Floors the current one leads to directly cross no ground at
// all, and hang from the first exit in room order.
//
// A floor's elevation still counts the fewest up/down steps to it (+1 per up, -1
// per down), and only floors within maxLevels of the current one are drawn — a
// floor placed by way of a deep shaft is no further away for it.
//
// Where floors share a cell it reads as a view from above: the higher floor is
// drawn and the deeper one is covered, on both sides of the current floor. Going
// up, the floor two up covers the floor one up; going down, the floor one down
// covers the floor two down; and anything above covers anything below, so a cave
// or volcano never covers the trail climbing over it (user, 2026-10-02 and
// 2026-10-04; report paradigm-20261004-204610 — nearest-first on the way up drew
// the lower floor over the higher). The current floor is never covered.
//
// A floor that would land mostly on cells already drawn is a different place
// stacked over this one (a volcano under the hills, barracks under a trail), and
// mixing it in only clutters the map, so past maxOverlap it is left out (user,
// 2026-10-02). It is weighed on its own: a mountain path is a chain of small
// floors, and dropping everything beyond one crowded landing would cut the path.
//
// Showing only the floors above (or below) still searches through both: a floor
// above can be reached by a path that dips first. The other side is dropped
// before it claims any cell. A floor level with the current one (up, then back
// down somewhere else) is on neither side and is drawn either way.
//
// Which floors are drawn at all is still settled nearest first, so the floor one
// step away is never the one left out for overlapping floors further off.
public static class OtherLevels
{
    public const int MaxRooms = 6000;

    // A floor with no more than this many rooms on drawn cells is never left out:
    // a small floor's stair landings alone can be most of it.
    public const int MinCoveredToHide = 4;

    // Rooms the placement search may take in looking for the chain that places a
    // drawn floor best. Past the drawn budget on purpose: that chain can run through
    // floors that are themselves too far up or down to draw. It normally ends long
    // before this, as soon as every drawn floor has its place.
    public const int MaxExploredRooms = 4 * MaxRooms;

    private sealed record Floor(RoomLayout Layout, RoomKey Landing, int Dx, int Dy, int Elevation, int Order,
        List<RoomKey> Rooms)
    {
        public (int, int, int) Rank => (Elevation < 0 ? 1 : 0, Math.Abs(Elevation), Order);
        public (int X, int Y) Cell(RoomKey k) => (Layout.Positions[k].X + Dx, Layout.Positions[k].Y + Dy);
    }

    // An up/down exit waiting to be followed by the placement search: the cell on
    // the current floor's grid its room sits on, where it leads, and the ground
    // crossed on other floors to get there.
    private readonly record struct Link((int X, int Y) At, RoomKey Target, int Crossed);

    // maxOverlap is the share (0..1) of a floor's rooms, past the one it's entered
    // by, that may land on drawn cells before it's left out; 1 keeps every floor.
    public static RoomLayout? Build(RoomLayout current, Func<RoomKey, Room?> roomOf,
        Func<RoomKey, RoomLayout> layoutFrom, int maxLevels, double maxOverlap,
        OtherFloorsMode show = OtherFloorsMode.Both, CancellationToken cancel = default,
        int maxExploredRooms = MaxExploredRooms)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(roomOf);
        ArgumentNullException.ThrowIfNull(layoutFrom);
        if (show == OtherFloorsMode.Off) return null;

        // One layout per floor, shared by both searches: the room it was first built
        // for and every room that build placed.
        Dictionary<RoomKey, RoomLayout> layoutOf = new();
        foreach (RoomKey k in current.Positions.Keys) layoutOf[k] = current;
        RoomLayout LayoutAt(RoomKey room)
        {
            if (layoutOf.TryGetValue(room, out RoomLayout? known)) return known;
            RoomLayout built = layoutFrom(room);
            foreach (RoomKey k in built.Positions.Keys) layoutOf.TryAdd(k, built);
            return built;
        }

        // Count: which floors are drawn, and each one's elevation by the fewest
        // up/down steps. A room belongs to the first floor that reaches it. The
        // offsets found here hang each floor from that same fewest-steps chain, and
        // stand only for a floor the placement search below doesn't get to.
        HashSet<RoomKey> reached = new(current.Positions.Keys);
        List<Floor> found = new();
        Queue<Floor> search = new();
        search.Enqueue(new Floor(current, current.Origin, 0, 0, 0, 0, current.Positions.Keys.ToList()));
        int owned = 0;
        while (search.TryDequeue(out Floor? floor) && owned < MaxRooms)
        {
            foreach (RoomKey key in floor.Rooms.OrderBy(k => k.Map).ThenBy(k => k.Room))
            {
                cancel.ThrowIfCancellationRequested();
                if (roomOf(key) is not { } room) continue;
                (int X, int Y) at = floor.Layout.Positions[key];
                foreach (Direction vertical in VerticalDirections)
                {
                    int elevation = floor.Elevation + (vertical == Direction.U ? 1 : -1);
                    if (Math.Abs(elevation) > maxLevels || owned >= MaxRooms) continue;
                    if (!room.Exits.TryGetValue(vertical, out RoomExit exit) || reached.Contains(exit.Target)) continue;
                    RoomLayout next = LayoutAt(exit.Target);
                    if (!next.Positions.TryGetValue(exit.Target, out (int X, int Y) landing)) continue;
                    List<RoomKey> rooms = next.Positions.Keys.Where(reached.Add).Take(MaxRooms - owned).ToList();
                    owned += rooms.Count;
                    Floor reachedFloor = new(next, exit.Target, at.X + floor.Dx - landing.X, at.Y + floor.Dy - landing.Y,
                        elevation, found.Count + 1, rooms);
                    found.Add(reachedFloor);
                    search.Enqueue(reachedFloor);
                }
            }
        }
        if (found.Count == 0) return null;

        // Place: follow up/down exits outward from the current floor, least ground
        // crossed first, until every drawn floor has been reached. The first chain to
        // reach a floor is the one that bends it least, and fixes where it sits.
        Dictionary<RoomLayout, (int Dx, int Dy, RoomKey Entry)> placedAt = new(ReferenceEqualityComparer.Instance);
        HashSet<RoomLayout> unplaced = new(found.Select(f => f.Layout), ReferenceEqualityComparer.Instance);
        unplaced.Remove(current);
        PriorityQueue<Link, (int Crossed, int Sequence)> open = new();
        int sequence = 0, explored = 0;

        void Open(RoomLayout layout, int dx, int dy, RoomKey entry, int crossed)
        {
            bool isCurrent = ReferenceEquals(layout, current);
            (int X, int Y) entryAt = layout.Positions.TryGetValue(entry, out (int X, int Y) e) ? e : default;
            foreach (RoomKey key in layout.Positions.Keys.OrderBy(k => k.Map).ThenBy(k => k.Room))
            {
                cancel.ThrowIfCancellationRequested();
                // A room an earlier floor's layout already placed is opened from there.
                if (!ReferenceEquals(layoutOf[key], layout) || roomOf(key) is not { } room) continue;
                (int X, int Y) at = layout.Positions[key];
                foreach (Direction vertical in VerticalDirections)
                {
                    if (!room.Exits.TryGetValue(vertical, out RoomExit exit)) continue;
                    if (layoutOf.TryGetValue(exit.Target, out RoomLayout? known) && placedAt.ContainsKey(known)) continue;
                    // The current floor is the grid everything else is measured on,
                    // so leaving it from anywhere crosses nothing.
                    int across = isCurrent ? 0 : Math.Max(Math.Abs(at.X - entryAt.X), Math.Abs(at.Y - entryAt.Y));
                    open.Enqueue(new Link((at.X + dx, at.Y + dy), exit.Target, crossed + across),
                        (crossed + across, sequence++));
                }
            }
        }

        placedAt[current] = (0, 0, current.Origin);
        Open(current, 0, 0, current.Origin, 0);
        while (unplaced.Count > 0 && explored < maxExploredRooms && open.TryDequeue(out Link link, out _))
        {
            cancel.ThrowIfCancellationRequested();
            RoomLayout next = LayoutAt(link.Target);
            if (placedAt.ContainsKey(next) || !next.Positions.TryGetValue(link.Target, out (int X, int Y) landing))
                continue;
            int dx = link.At.X - landing.X, dy = link.At.Y - landing.Y;
            placedAt[next] = (dx, dy, link.Target);
            unplaced.Remove(next);
            explored += next.Positions.Count;
            Open(next, dx, dy, link.Target, link.Crossed);
        }
        for (int i = 0; i < found.Count; i++)
            if (placedAt.TryGetValue(found[i].Layout, out (int Dx, int Dy, RoomKey Entry) spot))
                found[i] = found[i] with { Dx = spot.Dx, Dy = spot.Dy, Landing = spot.Entry };

        Dictionary<(int X, int Y), RoomKey> taken = new(current.CoordToRoom);
        Dictionary<RoomKey, (int X, int Y)> positions = new();
        Dictionary<(int X, int Y), RoomKey> coordToRoom = new();
        Dictionary<RoomKey, VerticalHint> hints = new();
        Dictionary<(int X, int Y), IReadOnlySet<Direction>> edges = new();
        Dictionary<(int X, int Y), IReadOnlySet<Direction>> traps = new();
        Dictionary<(int X, int Y), IReadOnlySet<Direction>> spells = new();
        // Pass 1, nearest first: which floors are drawn. Each is weighed against the
        // current floor and the floors already chosen.
        List<Floor> drawn = new();
        HashSet<(int X, int Y)> crowded = new(current.CoordToRoom.Keys);
        foreach (Floor floor in found.OrderBy(f => f.Rank))
        {
            cancel.ThrowIfCancellationRequested();
            if (floor.Elevation > 0 ? show == OtherFloorsMode.Down
                : floor.Elevation < 0 && show == OtherFloorsMode.Up) continue;
            int weighed = 0, covered = 0;
            foreach (RoomKey k in floor.Rooms)
            {
                if (k == floor.Landing) continue;
                weighed++;
                if (crowded.Contains(floor.Cell(k))) covered++;
            }
            if (covered > MinCoveredToHide - 1 && covered > weighed * maxOverlap) continue;
            drawn.Add(floor);
            foreach (RoomKey k in floor.Rooms) crowded.Add(floor.Cell(k));
        }

        // Pass 2, highest first: who gets a shared cell.
        foreach (Floor floor in drawn.OrderByDescending(f => f.Elevation).ThenBy(f => f.Order))
        {
            cancel.ThrowIfCancellationRequested();
            RoomLayout layout = floor.Layout;
            foreach (RoomKey k in floor.Rooms)
            {
                (int X, int Y) cell = floor.Cell(k);
                if (!taken.TryAdd(cell, k)) continue;
                (int X, int Y) p = layout.Positions[k];
                positions[k] = cell;
                coordToRoom[cell] = k;
                if (layout.VerticalHints.TryGetValue(k, out VerticalHint hint)) hints[k] = hint;
                if (layout.EdgesFromCoord.TryGetValue(p, out IReadOnlySet<Direction>? e)) edges[cell] = e;
                if (layout.TrapEdgesFromCoord.TryGetValue(p, out IReadOnlySet<Direction>? t)) traps[cell] = t;
                if (layout.SpellEdgesFromCoord.TryGetValue(p, out IReadOnlySet<Direction>? sp)) spells[cell] = sp;
            }
        }

        if (positions.Count == 0) return null;
        return new RoomLayout(
            Origin: current.Origin,
            Positions: positions,
            VerticalHints: hints,
            CoordToRoom: coordToRoom,
            EdgesFromCoord: edges,
            TrapEdgesFromCoord: traps,
            SpellEdgesFromCoord: spells)
        { LayoutRoot = current.LayoutRoot };
    }

    private static readonly Direction[] VerticalDirections = { Direction.U, Direction.D };
}
