using FujinTerm.Game.Map;

namespace FujinTerm.Models.Profile;

/// <summary>
/// JSON-friendly representation of a single tracked step taken from a
/// <see cref="Game.Map.RoomTracker"/> Confirmed state — one entry per
/// outbound move command. Persisted on <see cref="CharacterProfile.RecentSteps"/>
/// so the tracker can replay-from-last-known on session start.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Cardinal"/> carries the BFS-routable direction when the
/// step was a cardinal exit (the usual case). <see cref="Command"/> is
/// the exact text sent to the server; the look-direction-interception
/// work (separate unit) will populate this for arbitrary text exits
/// ("go path", "enter portal") that don't map to a <see cref="Direction"/>.
/// </para>
/// <para>
/// At least one of the two fields is non-null on any persisted entry —
/// a step with neither carries no information and would never be
/// written. The replayer prefers <see cref="Cardinal"/> when present;
/// it falls back to <see cref="Command"/> only for text exits.
/// </para>
/// </remarks>
public sealed class DirectionDto
{
    /// <summary>
    /// Cardinal direction of the step, when the move maps to one.
    /// <c>null</c> for arbitrary text-exit commands.
    /// </summary>
    public Direction? Cardinal { get; set; }

    /// <summary>
    /// Exact command string sent to the server. Used to replay
    /// text-exit steps verbatim. <c>null</c> when the step was a plain
    /// cardinal direction (the replayer resends a canonical short
    /// form like <c>"n"</c> derived from <see cref="Cardinal"/>).
    /// </summary>
    public string? Command { get; set; }

    public DirectionDto() { }

    public DirectionDto(Direction cardinal)
    {
        Cardinal = cardinal;
    }

    public DirectionDto(Direction? cardinal, string? command)
    {
        Cardinal = cardinal;
        Command = command;
    }
}
