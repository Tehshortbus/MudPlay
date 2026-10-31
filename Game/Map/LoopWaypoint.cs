using System.Text.Json.Serialization;

namespace MudPlay.Game.Map;

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
    public long DelayMs { get; set; }

    // When true, the loop must NOT rest in THIS room even if HP/MA drop below
    // the "rest if below" gates — it advances instead (a room too dangerous to
    // sit still in). Only this exact room is protected; the moment we step out
    // (into a BFS path room or an unmarked waypoint) resting resumes normally.
    // Defaults false, so older loop files without the field behave as before.
    public bool DoNotRest { get; set; }

    // When true, the loop must NOT engage hostiles in THIS room even if the
    // Min/Max monster-count window is met — it behaves as if auto-combat were
    // off and walks on. Only this exact room is skipped. The one exception: if
    // a rest is triggered here (HP/MA below its gate), the client still clears
    // the room so the rest can proceed. Defaults false, so older loop files
    // without the field behave as before.
    public bool DoNotAttack { get; set; }

    // "Rest up here": on arriving, rest until HP (RestHereHp) / mana (RestHereMana) is
    // back to its rest-max ("rest to" on the Health tab) before moving on, whatever
    // the "rest if below" trigger says — unless it's already there. Carried over from
    // MegaMUD's per-step "Rest up here". Defaults false.
    public bool RestHereHp { get; set; }
    public bool RestHereMana { get; set; }

    // Wait here, after Command if there is one, until the current lap has run this
    // many milliseconds, so a lap never comes round sooner (a lair's respawn, say).
    // 0 = no wait. Skipped on a lap that isn't counted (a run joined partway round
    // has no lap clock to read). Independent of DelayMs, which counts from the
    // command, not from the lap's start.
    public long LapWaitMs { get; set; }

    // "When starting this loop, always go to this room first": a fresh start walks
    // here and begins the lap at this room, wherever it was started from, even
    // standing on the loop elsewhere (a daily loop that has to open in its first
    // room; user, 2026-10-31). A resume or a recovery joins where it always did. One
    // per loop: the editors clear it from the others, and the first one counts.
    public bool StartHere { get; set; }

    // Parsed RoomKey from Room. Default when malformed.
    [JsonIgnore]
    public RoomKey Key => RoomKey.TryParseWire(Room, out RoomKey k) ? k : default;

    public LoopWaypoint() { }

    public LoopWaypoint(RoomKey key, string? command = null, long delayMs = 0, bool doNotRest = false, bool doNotAttack = false)
    {
        Room        = key.ToString();
        Command     = command;
        DelayMs     = delayMs;
        DoNotRest   = doNotRest;
        DoNotAttack = doNotAttack;
    }
}
