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

/// <summary>A planar movement — sends the lowercase direction over the wire and waits for the room to change.</summary>
public sealed record MoveStep(Direction Direction, RoomKey ExpectedTarget) : WalkStep
{
    public override string Display => Direction switch
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
