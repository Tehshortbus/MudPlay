using System.Collections.Generic;

namespace FujinTerm.Models.Profile;

/// <summary>
/// Persisted Auto-Lair "setup" — a named bundle of marked lair rooms
/// the scheduler cycles through. Mirrors <see cref="Game.Map.Loop"/>'s
/// role for loops: storage shape, not runtime state. The
/// <see cref="Game.Map.LairManager"/> round-trips one file per setup
/// under <see cref="Services.AppPaths.BbsLairsFolder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why BBS-scoped, not per-character</b>: lair locations are game-
/// data facts (where the spawn lives), not personal preferences. A
/// "lower-sewers rats" setup is reusable by every character on the
/// same BBS without copying. Per-character preference belongs in
/// <see cref="Models.Profile.AutoLairSettings"/> instead.
/// </para>
/// <para>
/// <b>Why unordered markers</b>: the scheduler picks the next lair
/// each tick based on respawn timers + travel cost. The user's "add"
/// order doesn't constrain execution order, and re-ordering markers
/// would be misleading UI.
/// </para>
/// </remarks>
public sealed class LairSetup
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public List<LairMarker> Markers { get; set; } = new();

    public LairSetup() { }

    public LairSetup(string name, IEnumerable<LairMarker> markers)
    {
        Name = name ?? string.Empty;
        Markers = new List<LairMarker>(markers ?? Array.Empty<LairMarker>());
    }

    /// <summary>Marker count — read-only convenience for UI bindings.</summary>
    public int MarkerCount => Markers.Count;
}
