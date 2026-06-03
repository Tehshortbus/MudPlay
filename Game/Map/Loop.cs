using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Serialization;

namespace FujinTerm.Game.Map;

/// <summary>
/// One saved navigation loop. The Navigation right-rail "Loops"
/// section lists every loop loaded by <see cref="LoopManager"/>;
/// double-clicking runs it (PR 7.16). Loops are per-BBS — the same
/// realm-graph means a loop saved on one character is usable by
/// every character connected to that BBS.
/// </summary>
/// <remarks>
/// <para>
/// A loop is <i>circular</i> when its first and last meaningful room
/// are the same — the runner can repeat it indefinitely without
/// needing a "return to start" path. <see cref="IsCircular"/> is a
/// hint surfaced by the editor / runner; it isn't validated against
/// <see cref="Steps"/> here because the runner needs to handle both
/// shapes (one-shot and circular) anyway.
/// </para>
/// <para>
/// <see cref="LastRunAt"/> drives the "2h ago" / "yesterday" badge in
/// the right-rail loop list. <see cref="LastModifiedAt"/> stamps every
/// save so the editor can warn on stale edits.
/// </para>
/// </remarks>
public sealed class Loop
{
    public required string Name { get; set; }
    public required List<LoopStep> Steps { get; set; }

    public bool IsCircular { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Display name for the right-rail badge — e.g. <c>"4 rooms · L3"</c> (level is editor-side metadata).</summary>
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
