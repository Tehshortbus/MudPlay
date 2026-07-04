using System.Text.Json.Serialization;

namespace FujinTerm.Game.Map;

// One point in a saved Loop's ordered cycle. Carries the room the user
// clicked plus an optional in-room command to send after arrival (with a
// delay before advancing to the next leg). Replaces v2's separate
// UserWaypoints + Steps + inline CommandLoopStep shape — every command
// attaches to the waypoint it logically belongs to, and the runtime move
// sequence is recomputed via BFS at run-time.
//
// Room is the wire-form "{map}/{room}" string (the same shape
// RoomKey.ToString emits) so saved loops are readable by hand. Key exposes
// the parsed form for code.
//
// Plain mutable POCO so System.Text.Json round-trips it without a
// converter; matches the rest of the app's per-file settings model.
public sealed class LoopWaypoint
{
    // Wire form, e.g. "1/5".
    public string Room { get; set; } = string.Empty;

    // Optional in-room command to send after arriving at this waypoint.
    // Null / empty = no command. Fires on every lap.
    public string? Command { get; set; }

    // Delay in milliseconds after sending Command before advancing to the
    // next waypoint. Ignored when Command is null / empty. 0 means "advance
    // on the next prompt" (same contract as v2 CommandLoopStep.DelayMs == 0).
    public int DelayMs { get; set; }

    // Parsed RoomKey from Room. Default when malformed.
    [JsonIgnore]
    public RoomKey Key => RoomKey.TryParseWire(Room, out RoomKey k) ? k : default;

    public LoopWaypoint() { }

    public LoopWaypoint(RoomKey key, string? command = null, int delayMs = 0)
    {
        Room    = key.ToString();
        Command = command;
        DelayMs = delayMs;
    }
}
