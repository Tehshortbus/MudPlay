using FujinTerm.Game.Map;

namespace FujinTerm.Game.Remote;

/// <summary>Which movement engine, if any, is currently driving the wire.</summary>
public enum MovementKind
{
    /// <summary>No engine running — the player is moving manually or idle.</summary>
    None,

    /// <summary>A one-shot <see cref="AutoWalkManager"/> walk-to is in progress.</summary>
    Walking,

    /// <summary>A <see cref="LoopRunner"/> circuit is running.</summary>
    Loop,

    /// <summary>The <see cref="AutoLairManager"/> scheduler is active.</summary>
    Lair,
}

/// <summary>
/// A cross-engine snapshot of "what is moving me right now" for the
/// <c>@path</c> remote-command reply. Captures the topmost active engine
/// plus the walker's step progress so a party member can ask where the
/// leader's automation has got to.
/// </summary>
/// <param name="Kind">Which engine is active (or <see cref="MovementKind.None"/>).</param>
/// <param name="Label">
/// Human-readable engine subject: the loop's name for
/// <see cref="MovementKind.Loop"/>, the destination <c>map/room</c> for
/// <see cref="MovementKind.Walking"/>, a fixed <c>"auto-lair"</c> for
/// <see cref="MovementKind.Lair"/>, <c>null</c> for
/// <see cref="MovementKind.None"/>.
/// </param>
/// <param name="CurrentStep">
/// Zero-based index of the next walk step to send (the walker's
/// <see cref="AutoWalkManager.CurrentStepIndex"/>). Reported one-based in
/// the reply.
/// </param>
/// <param name="TotalSteps">
/// Total steps in the active walk path
/// (<see cref="AutoWalkManager.StepCount"/>); <c>0</c> when no path is loaded.
/// </param>
public readonly record struct MovementStatus(
    MovementKind Kind,
    string? Label,
    int CurrentStep,
    int TotalSteps)
{
    /// <summary>
    /// Snapshot the running movement engine. Priority Lair → Loop →
    /// Walker mirrors <c>PartyComebackManager.SnapshotRunningEngine</c>:
    /// the upper engines drive the lower ones (Auto-Lair drives the
    /// walker; a loop drives the walker during its approach leg), so the
    /// topmost active engine is the real activity to report. Step counts
    /// always come from the walker because every engine ultimately moves
    /// through it. Any null argument (engines not constructed yet — pre
    /// game-data-load) yields <see cref="MovementKind.None"/>.
    /// </summary>
    public static MovementStatus Capture(
        AutoWalkManager? walker,
        LoopRunner? loopRunner,
        AutoLairManager? autoLair)
    {
        if (walker is null || loopRunner is null || autoLair is null)
            return new MovementStatus(MovementKind.None, null, 0, 0);

        if (autoLair.IsActive)
            return new MovementStatus(MovementKind.Lair, "auto-lair",
                walker.CurrentStepIndex, walker.StepCount);

        if (loopRunner.State is not LoopState.Idle && loopRunner.CurrentLoop is { } loop)
            return new MovementStatus(MovementKind.Loop, loop.Name,
                loopRunner.CurrentIndex, loopRunner.StepCount);

        if (walker.State is not WalkState.Idle && walker.Destination is { } dest)
            return new MovementStatus(MovementKind.Walking, $"{dest.Map}/{dest.Room}",
                walker.CurrentStepIndex, walker.StepCount);

        return new MovementStatus(MovementKind.None, null, 0, 0);
    }
}
