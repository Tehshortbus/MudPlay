using System.Text.Json.Serialization;

namespace FujinTerm.Game.Map;

/// <summary>
/// One point in a saved <see cref="Loop"/>'s ordered cycle. Carries
/// the room the user clicked plus an optional in-room command to send
/// after arrival (with a delay before advancing to the next leg).
/// Replaces v2's separate <c>UserWaypoints</c> + <c>Steps</c> +
/// inline <c>CommandLoopStep</c> shape — every command attaches to
/// the waypoint it logically belongs to, and the runtime move
/// sequence is recomputed via BFS at run-time.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Room"/> is the wire-form <c>"{map}/{room}"</c> string
/// (the same shape <see cref="RoomKey.ToString"/> emits) so saved
/// loops are readable by hand. <see cref="Key"/> exposes the parsed
/// form for code.
/// </para>
/// <para>
/// Plain mutable POCO so <see cref="System.Text.Json"/> round-trips
/// it without a converter; matches the rest of FujinTerm's per-file
/// settings model.
/// </para>
/// </remarks>
public sealed class LoopWaypoint
{
    /// <summary>Wire form, e.g. <c>"1/5"</c>.</summary>
    public string Room { get; set; } = string.Empty;

    /// <summary>
    /// Optional in-room command to send after arriving at this
    /// waypoint. Null / empty = no command. Fires on every lap.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Delay in milliseconds after sending <see cref="Command"/>
    /// before advancing to the next waypoint. Ignored when
    /// <see cref="Command"/> is null / empty. <c>0</c> means
    /// "advance on the next prompt" (same contract as v2
    /// <c>CommandLoopStep.DelayMs == 0</c>).
    /// </summary>
    public int DelayMs { get; set; }

    /// <summary>Parsed <see cref="RoomKey"/> from <see cref="Room"/>. Default when malformed.</summary>
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
