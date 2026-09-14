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
// reachable when they're honoured. That difference is exactly "rooms I could
// reach if not for my level", which is what the overlay claims — and it can't
// be confused with rooms that are unreachable for some other reason (a
// disconnected graph, a teleport-only pocket), because those are absent from
// both sweeps and cancel out.
//
// Position matters: the gate is tested on ENTRY, so a low-level character
// already inside the Arena is not blocked from it. Recompute when the level or
// the current room changes.
public static class LevelBlockedRooms
{
    // Empty when the graph is empty, the origin is unknown, or the level is
    // unknown — an unknown level never gates, matching MovementFilter's
    // "don't refuse on what we can't evaluate" rule.
    public static IReadOnlySet<RoomKey> Compute(RoomGraphManager graph, RoomKey? origin, int level)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (origin is not { } from || level <= 0) return EmptySet;

        HashSet<RoomKey> ignoringGates = Reachable(graph, from, level, honourGates: false);
        if (ignoringGates.Count == 0) return EmptySet;

        HashSet<RoomKey> honouringGates = Reachable(graph, from, level, honourGates: true);
        ignoringGates.ExceptWith(honouringGates);
        return ignoringGates;
    }

    private static HashSet<RoomKey> Reachable(
        RoomGraphManager graph, RoomKey from, int level, bool honourGates)
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
                if (honourGates && ExcludedByLevel(exit, level)) continue;
                if (seen.Add(exit.Target)) pending.Push(exit.Target);
            }
        }

        return seen;
    }

    // Mirrors MovementFilter's self-level branch: a zero bound means no bound on
    // that side, and the MDB's 0/999 sentinels are already normalised away at
    // parse time.
    private static bool ExcludedByLevel(in RoomExit exit, int level)
    {
        if (!exit.HasLevelGate) return false;
        if (exit.MinLevel > 0 && level < exit.MinLevel) return true;
        return exit.MaxLevel > 0 && level > exit.MaxLevel;
    }

    private static readonly IReadOnlySet<RoomKey> EmptySet = new HashSet<RoomKey>();
}
