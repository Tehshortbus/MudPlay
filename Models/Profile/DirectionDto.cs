using FujinTerm.Game.Map;

namespace FujinTerm.Models.Profile;

// JSON-friendly representation of a single tracked step taken from a
// Game.Map.RoomTracker Confirmed state — one entry per outbound move command.
// Persisted on CharacterProfile.RecentSteps so the tracker can
// replay-from-last-known on session start.
//
// Cardinal carries the BFS-routable direction when the step was a cardinal exit
// (the usual case). Command is the exact text sent to the server; the
// look-direction-interception work populates this for arbitrary text exits
// ("go path", "enter portal") that don't map to a Direction.
//
// At least one of the two fields is non-null on any persisted entry — a step
// with neither carries no information and would never be written. The replayer
// prefers Cardinal when present; it falls back to Command only for text exits.
public sealed class DirectionDto
{
    // Cardinal direction of the step, when the move maps to one. null for
    // arbitrary text-exit commands.
    public Direction? Cardinal { get; set; }

    // Exact command string sent to the server. Used to replay text-exit steps
    // verbatim. null when the step was a plain cardinal direction (the replayer
    // resends a canonical short form like "n" derived from Cardinal).
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
