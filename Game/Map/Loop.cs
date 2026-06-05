using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Serialization;

namespace FujinTerm.Game.Map;

/// <summary>
/// One saved navigation loop. The Navigation right-rail "Loops"
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
/// "go from A to B" is the walker's job (right-click → Walk to);
/// the loop runner has no "Finished" event.
/// </para>
/// <para>
/// <see cref="UserWaypoints"/> preserves the user's original click
/// sequence separately from the BFS-expanded <see cref="Steps"/>.
/// This is load-bearing for two flows: (a) re-opening a loop in the
/// editor without polluting the click list with auto-filled
/// intermediate hops, and (b) re-expanding the loop mid-run when the
/// avoided-rooms list changes so the circle stays clean.
/// </para>
/// <para>
/// <see cref="LastRunAt"/> drives the "2h ago" / "yesterday" badge in
/// the right-rail loop list. <see cref="LastModifiedAt"/> stamps every
/// save so the editor can warn on stale edits.
/// </para>
/// </remarks>
public sealed class Loop
{
    /// <summary>
    /// Serialised loop schema version. v1 = original (had IsCircular,
    /// no UserWaypoints / Notes); v2 = current (all-circular, with
    /// UserWaypoints + Notes). <see cref="LoopManager.LoadAll"/>
    /// upgrades v1 records in memory on load.
    /// </summary>
    public int SchemaVersion { get; set; } = 2;

    public required string Name { get; set; }
    public required List<LoopStep> Steps { get; set; }

    /// <summary>
    /// Original click sequence the user laid down in the builder, in
    /// order, closing edge implicit. Empty for v1 loops loaded from
    /// disk before the v2 upgrade — those can still run but can't be
    /// re-expanded on avoid-list change until the user re-saves them
    /// in the editor.
    /// </summary>
    public List<RoomKey> UserWaypoints { get; set; } = new();

    /// <summary>Free-form user notes. Empty by default.</summary>
    public string Notes { get; set; } = string.Empty;

    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Display name for the right-rail badge — e.g. <c>"4 rooms"</c>.</summary>
    [JsonIgnore]
    public int RoomCount => Steps.Count(s => s is MoveLoopStep);

    // ----- helpers (used by LoopManager + tests) ---------------------

    [SetsRequiredMembers]
    public Loop() { Name = ""; Steps = new(); }

    [SetsRequiredMembers]
    public Loop(string name, IEnumerable<LoopStep> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(steps);
        Name = name;
        Steps = new List<LoopStep>(steps);
    }
}
