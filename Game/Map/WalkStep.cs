namespace FujinTerm.Game.Map;

// One unit of work in an expanded walk path. Either a movement (single
// direction whose execution changes the room) or a free-text command that the
// game requires before the next move can succeed (door open, lever pull, etc.).
public abstract record WalkStep
{
    // Short label for the Navigation right-rail step list (e.g. "north",
    // "open door east").
    public abstract string Display { get; }
}

// A planar movement. Normally sends the lowercase direction over the wire and
// waits for the room to change. When CommandLabel is set the exit is traversed
// by a fixed game-data command instead of the cardinal (e.g. a
// (Text: borrow skiff) exit), and that command — not the direction — is what the
// walker sends and what the step list shows.
//
// CommandLabel is the exact command the exit requires in place of the cardinal,
// when game data statically pins one (Text exits). null for ordinary passages
// and for exits whose command is only known at runtime (teleport keywords, door
// opens) — those still display the direction.
public sealed record MoveStep(Direction Direction, RoomKey ExpectedTarget, string? CommandLabel = null) : WalkStep
{
    // Cross this as a plain cardinal even when the exit is a synchronous
    // special exit. The cross-room multi-action expander sets it on the final
    // cardinal of an exit whose prerequisite commands it already emitted as
    // explicit CommandSteps — letting the send-side multi-action dispatch run
    // again would double-issue those commands (and choke on the remote ones).
    public bool SkipSpecialDispatch { get; init; }

    public override string Display => CommandLabel ?? Direction switch
    {
        Map.Direction.N  => "north",
        Map.Direction.S  => "south",
        Map.Direction.E  => "east",
        Map.Direction.W  => "west",
        Map.Direction.NE => "northeast",
        Map.Direction.NW => "northwest",
        Map.Direction.SE => "southeast",
        Map.Direction.SW => "southwest",
        Map.Direction.U  => "up",
        Map.Direction.D  => "down",
        _ => "?",
    };
}

// A free-text command the walker must send and have the server acknowledge
// before sending the next step. Door-open prerequisites are the only kind
// RemoteActionPathExpander emits today; lever pulls / button presses could
// follow when game data describes the prerequisite.
//
// Command is the exact text to send (no trailing CR — the walker appends it).
public sealed record CommandStep(string Command) : WalkStep
{
    public override string Display => Command;
}
