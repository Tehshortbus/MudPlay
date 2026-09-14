namespace MudPlay.Game.Map;

// Which rooms this character's LEVEL shuts them out of, given where they're
// standing. Backs the map's level-blocked overlay.
//
// The answer isn't just "rooms behind a gated exit" — a gate seals everything
// that can only be reached through it. Newhaven's Arena is the shape: one gated
// way in (1/2146 D, levels 1-3), and the Dungeon Entrance and Small Cavern
// beyond it have no other entrance, so an over-level character is shut out of
// the whole pocket, not one room.
//
// Computed as a difference of two reachability sweeps from the player's current
// room: everything reachable when level gates are ignored, minus everything
// reachable when they're honoured.
//
// Every OTHER obstacle — doors, locks, keys, tolls, class and alignment windows,
// hazards — is applied IDENTICALLY in both sweeps. That is what keeps this a
// purely level-based overlay: a non-level gate cancels out of the difference, so
// it can never paint a room and can never suppress one. It only stops the sweep
// routing through a wall that isn't really there. Without that, a pocket whose
// sole "alternative" entrance is a door nobody can open (the MDB's
// 1000-picklocks sentinel) looks reachable and silently goes unpainted — the
// Ancient Fortress on Paradigm is exactly that shape, sealed behind a level-75
// gate but "reachable" through an impassable pyramid door.
//
// Judging that is the movement filter's job, not ours: pass the same IRoomFilter
// the router uses and the overlay agrees with the route picker by construction.
// Without one it falls back to level-only, which is right for a graph with no
// player context behind it.
//
// Position matters: the gate is tested on ENTRY, so a low-level character
// already inside the Arena is not blocked from it. Recompute when the level or
// the current room changes.
public static class LevelBlockedRooms
{
    // Empty when the graph is empty, the origin is unknown, or the level is
    // unknown — an unknown level never gates, matching MovementFilter's
    // "don't refuse on what we can't evaluate" rule.
    public static IReadOnlySet<RoomKey> Compute(
        RoomGraphManager graph, IRoomFilter? filter, RoomKey? origin, int level)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (origin is not { } from || level <= 0) return EmptySet;

        HashSet<RoomKey> ignoringGates = Reachable(graph, filter, from, level, honourLevel: false);
        if (ignoringGates.Count == 0) return EmptySet;

        HashSet<RoomKey> honouringGates = Reachable(graph, filter, from, level, honourLevel: true);
        ignoringGates.ExceptWith(honouringGates);
        return ignoringGates;
    }

    private static HashSet<RoomKey> Reachable(
        RoomGraphManager graph, IRoomFilter? filter, RoomKey from, int level, bool honourLevel)
    {
        HashSet<RoomKey> seen = new() { from };
        Stack<RoomKey> pending = new();
        pending.Push(from);

        while (pending.Count > 0)
        {
            RoomKey current = pending.Pop();
            if (graph.GetRoom(current) is not { } room) continue;

            foreach (RoomExit exit in room.Exits.Values)
            {
                // The origin itself is never "blocked" — we're standing in it —
                // so only the far side of an exit is gated. Crossing OUT of a
                // gated room is unrestricted; the game tests entry, not exit.
                if (Blocks(ExitReasons(filter, in exit, level), honourLevel)) continue;
                if (seen.Add(exit.Target)) pending.Push(exit.Target);
            }

            // A dock's sailings are a way onward the exit list can't express —
            // several sailings share one dock and the single Teleport slot holds
            // only one — so a level-gated captain needs its own step here or a
            // port reachable only by boat cancels out of both sweeps.
            foreach (BoatPassage passage in graph.BoatPassagesAt(current))
            {
                if (Blocks(BoatReasons(filter, in passage, level), honourLevel)) continue;
                if (seen.Add(passage.ArrivalRoom)) pending.Push(passage.ArrivalRoom);
            }
        }

        return seen;
    }

    // Level is the only reason allowed to differ between the two passes. Masking
    // it out of the ignore-pass — rather than skipping the filter entirely — is
    // what makes the difference mean "blocked BECAUSE of level".
    private static bool Blocks(ExitBlockReason reasons, bool honourLevel)
        => honourLevel
            ? reasons != ExitBlockReason.None
            : (reasons & ~ExitBlockReason.Level) != ExitBlockReason.None;

    private static ExitBlockReason ExitReasons(IRoomFilter? filter, in RoomExit exit, int level)
        => filter?.DescribeExitBlock(in exit)
           ?? (exit.ExcludesLevel(level) ? ExitBlockReason.Level : ExitBlockReason.None);

    private static ExitBlockReason BoatReasons(IRoomFilter? filter, in BoatPassage passage, int level)
        => filter?.DescribeBoatBlock(in passage)
           ?? (passage.MinLevel > 0 && level < passage.MinLevel
                   ? ExitBlockReason.Level
                   : ExitBlockReason.None);

    private static readonly IReadOnlySet<RoomKey> EmptySet = new HashSet<RoomKey>();
}
