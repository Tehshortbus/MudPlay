namespace MudPlay.Game.Map;

// The two graph probes a FootprintMatcher needs, factored out so the engine-driven
// recovery gate (EngineRecoveryGate's tier-2/3 localisers) and the engine-independent
// passive re-localiser inside RoomTracker narrow candidates with byte-identical logic
// rather than two drifting copies. Pure over the graph — no per-caller state.
internal static class GraphFootprintProbes
{
    // One candidate's hop in a direction. Distinguishes "no exit there" from
    // "exit there but trap-gated" — the matcher refuses to traverse traps, so a
    // trapped arm drops the candidate rather than following it blindly.
    public static HopOutcome Hop(RoomGraphManager graph, RoomKey from, Direction dir)
    {
        Room? source = graph.GetRoom(from);
        if (source is null) return HopOutcome.NoExit();
        if (!source.Exits.TryGetValue(dir, out RoomExit exit)) return HopOutcome.NoExit();
        return Through(exit);
    }

    // A hop through an exit the caller already picked out of the source room (a
    // text exit, found by its command rather than by a direction).
    public static HopOutcome Through(in RoomExit exit) =>
        exit.Hint == RoomExitHint.Trap ? HopOutcome.TrappedExit() : HopOutcome.Reached(exit.Target);

    // A candidate room matches an observation when the names are equal and every
    // exit the live display shows is present in the graph room (subset — the graph
    // may know exits the display hides behind a closed door / search).
    public static bool Matches(RoomGraphManager graph, RoomKey key, RoomObservation obs)
    {
        Room? r = graph.GetRoom(key);
        if (r is null) return false;
        if (!string.Equals(r.Name, obs.Name, StringComparison.OrdinalIgnoreCase)) return false;
        uint observedMask = 0;
        foreach (Direction d in obs.Exits) observedMask |= 1u << (int)d;
        return (observedMask & r.ExitMask) == observedMask;
    }
}
