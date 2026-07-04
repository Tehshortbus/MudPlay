using FujinTerm.Game.Map;

namespace FujinTerm.Game.Remote;

// Which movement engine, if any, is currently driving the wire.
public enum MovementKind
{
    // No engine running — the player is moving manually or idle.
    None,

    // A one-shot AutoWalkManager walk-to is in progress.
    Walking,

    // A LoopRunner circuit is running.
    Loop,

    // The AutoLairManager scheduler is active.
    Lair,
}

// A cross-engine snapshot of "what is moving me right now" for the @path
// remote-command reply. Captures the topmost active engine plus the walker's step
// progress so a party member can ask where the leader's automation has got to.
//
// Label is the human-readable engine subject: the loop's name for Loop, the
// destination map/room for Walking, a fixed "auto-lair" for Lair, null for None.
// CurrentStep is the zero-based index of the next walk step to send (the walker's
// CurrentStepIndex); reported one-based in the reply. TotalSteps is the total
// steps in the active walk path (StepCount); 0 when no path is loaded.
public readonly record struct MovementStatus(
    MovementKind Kind,
    string? Label,
    int CurrentStep,
    int TotalSteps)
{
    // Snapshot the running movement engine. Priority Lair → Loop → Walker mirrors
    // PartyComebackManager.SnapshotRunningEngine: the upper engines drive the
    // lower ones (Auto-Lair drives the walker; a loop drives the walker during its
    // approach leg), so the topmost active engine is the real activity to report.
    // Step counts always come from the walker because every engine ultimately
    // moves through it. Any null argument (engines not constructed yet — pre
    // game-data-load) yields MovementKind.None.
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
