namespace FujinTerm.Game.Map;

/// <summary>
/// One unit of work in an expanded walk path. Either a movement
/// (single direction whose execution changes the room) or a free-text
/// command that the game requires before the next move can succeed
/// (door open, lever pull, etc.).
/// </summary>
public abstract record WalkStep
{
    /// <summary>Short label for the Navigation right-rail step list (e.g. <c>"north"</c>, <c>"open door east"</c>).</summary>
    public abstract string Display { get; }
}

/// <summary>
/// A planar movement. Normally sends the lowercase direction over the
/// wire and waits for the room to change. When <see cref="CommandLabel"/>
/// is set the exit is traversed by a fixed game-data command instead of
/// the cardinal (e.g. a <c>(Text: borrow skiff)</c> exit), and that
/// command — not the direction — is what the walker sends and what the
/// step list shows.
/// </summary>
/// <param name="CommandLabel">
/// The exact command the exit requires in place of the cardinal, when
/// game data statically pins one (Text exits). <c>null</c> for ordinary
/// passages and for exits whose command is only known at runtime
/// (teleport keywords, door opens) — those still display the direction.
/// </param>
public sealed record MoveStep(Direction Direction, RoomKey ExpectedTarget, string? CommandLabel = null) : WalkStep
{
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

/// <summary>
/// A free-text command the walker must send and have the server
/// acknowledge before sending the next step. Door-open prerequisites
/// are the only kind <see cref="RemoteActionPathExpander"/> emits in
/// PR 7.7b; later phases may add lever pulls / button presses when
/// game data describes the prerequisite.
/// </summary>
/// <param name="Command">Exact text to send (no trailing CR — the walker appends it).</param>
public sealed record CommandStep(string Command) : WalkStep
{
    public override string Display => Command;
}
