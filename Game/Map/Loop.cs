using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FujinTerm.Game.Map;

/// <summary>
/// One saved navigation loop — the user's clicked-room cycle plus
/// optional per-waypoint commands. The Navigation right-rail "Loops"
/// section lists every loop loaded by <see cref="LoopManager"/>;
/// double-clicking runs it. Loops are per-BBS — the same realm-graph
/// means a loop saved on one character is usable by every character
/// connected to that BBS.
/// </summary>
/// <remarks>
/// <para>
/// All loops are circular by definition — they end at the room they
/// started in and repeat until the user stops them or the engine
/// recovery gate's tier-3 backtrack fails terminally. One-shot
/// "go from A to B" is the walker's job; the loop runner has no
/// "Finished" event.
/// </para>
/// <para>
/// <see cref="Waypoints"/> is the single source of truth for both
/// the rendered cycle and the runtime step list. The runner re-runs
/// BFS at start-time to expand the move sequence between consecutive
/// waypoints, so loops automatically pick up graph improvements
/// between sessions without re-saving. Commands attach to waypoints
/// (fire on arrival) rather than living at arbitrary positions in a
/// flat step list — simpler to read on disk and matches the natural
/// use case ("rest at this room", "ask barmaid here", etc.).
/// </para>
/// </remarks>
public sealed class Loop
{
    /// <summary>
    /// Serialised loop schema version.
    /// <list type="bullet">
    ///   <item>v1: original (had IsCircular, flat Steps list).</item>
    ///   <item>v2: dropped IsCircular, added UserWaypoints + Notes.</item>
    ///   <item>v3: current. Collapsed UserWaypoints+Steps into a
    ///         single <see cref="Waypoints"/> list with per-waypoint
    ///         commands; dropped LastRunAt / LastModifiedAt.</item>
    /// </list>
    /// <see cref="LoopManager.LoadAll"/> upgrades older records in
    /// memory on load.
    /// </summary>
    public int SchemaVersion { get; set; } = 3;

    public required string Name { get; set; }

    /// <summary>
    /// Ordered cycle of waypoints. At runtime the loop runner
    /// BFS-fills the move sequence between consecutive entries plus
    /// the closing leg back to entry 0. Two-or-more entries required
    /// to form a runnable cycle.
    /// </summary>
    public List<LoopWaypoint> Waypoints { get; set; } = new();

    /// <summary>Free-form user notes. Empty by default.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Folder this loop lives under inside the BBS Loops directory,
    /// relative to it, using <c>/</c> separators (e.g.
    /// <c>"Sewers/Lower"</c>). Empty = the Loops root. Not serialised —
    /// the on-disk subdirectory is the source of truth;
    /// <see cref="LoopManager"/> sets this from the file's location on
    /// load and writes the file into the matching subdirectory on save.
    /// </summary>
    [JsonIgnore]
    public string Folder { get; set; } = string.Empty;

    /// <summary>
    /// Catches old-schema fields on load so
    /// <see cref="LoopManager.LoadAll"/> can run the v1/v2 → v3
    /// migration (UserWaypoints → Waypoints). Cleared after the
    /// upgrade runs and never written back to disk on save —
    /// JsonIgnore on the setter would defeat the receive path, so
    /// we rely on the upgrade clearing the dictionary instead.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? LegacyFields { get; set; }

    /// <summary>
    /// Display sub-label for the right-rail list — number of
    /// waypoints in the cycle (the user-clicked rooms, not the
    /// BFS-filled intermediates).
    /// </summary>
    [JsonIgnore]
    public int RoomCount => Waypoints.Count;

    // ----- helpers (used by LoopManager + tests) ---------------------

    [SetsRequiredMembers]
    public Loop() { Name = ""; }

    [SetsRequiredMembers]
    public Loop(string name, IEnumerable<LoopWaypoint> waypoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(waypoints);
        Name = name;
        Waypoints = new List<LoopWaypoint>(waypoints);
    }

    [SetsRequiredMembers]
    public Loop(string name, IEnumerable<RoomKey> waypointKeys)
        : this(name, ToWaypoints(waypointKeys)) { }

    private static IEnumerable<LoopWaypoint> ToWaypoints(IEnumerable<RoomKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (RoomKey k in keys) yield return new LoopWaypoint(k);
    }
}
