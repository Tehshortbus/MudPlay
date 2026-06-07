namespace FujinTerm.Game.Map;

/// <summary>
/// Which of the three concurrent movement engines is about to take
/// over the wire — the others get stopped so the chosen one owns the
/// command stream uncontested.
/// </summary>
/// <remarks>
/// <see cref="Walker"/> is the special case: <see cref="AutoWalkManager.WalkTo"/>
/// already supersedes its own prior plan, so the walker is NOT stopped
/// when keeping it. The loop and lair cases stop the walker because a
/// goto-walk left in flight would otherwise drive the wire while
/// <see cref="LoopRunner"/> or <see cref="AutoLairManager"/> is also
/// trying to.
/// </remarks>
internal enum SupersedeKeep
{
    Walker,
    Loop,
    Lair,
}

/// <summary>
/// Stop whichever of the three concurrent movement engines would
/// collide with the engine that's about to start. Shared by
/// <see cref="FujinTerm.Game.Remote.MovePlayerHandler"/> (remote
/// @goto / @loop / @lair) and
/// <see cref="FujinTerm.Game.Events.EventManager"/> (scheduled
/// walk-to / loop / auto-lair events).
/// </summary>
/// <remarks>
/// Without this, a second engine spinning up while a prior one is
/// still issuing wire commands produces interleaved moves and a
/// confused state machine on both sides. See
/// <see cref="FujinTerm.Game.Remote.MovePlayerHandler"/>'s in-source
/// comment for the canonical narrative.
/// </remarks>
internal static class EngineSupersede
{
    public static void StopOthers(
        AutoWalkManager? walker,
        LoopRunner? loopRunner,
        AutoLairManager? autoLair,
        SupersedeKeep keep,
        string reason)
    {
        if (keep != SupersedeKeep.Loop
            && loopRunner is { State: not LoopState.Idle })
        {
            loopRunner.Stop(reason);
        }
        if (keep != SupersedeKeep.Lair && autoLair is { IsActive: true })
        {
            autoLair.Stop(reason);
        }
        // Walker only needs explicit stop when keeping a different
        // engine — WalkTo already supersedes its own prior plan on the
        // walker-keep path, but a new loop / lair would otherwise leave
        // a goto-walk in flight when their first BFS hop fires.
        if (keep != SupersedeKeep.Walker
            && walker is { State: not WalkState.Idle })
        {
            walker.Stop(reason);
        }
    }
}
