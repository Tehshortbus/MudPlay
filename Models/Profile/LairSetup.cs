using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FujinTerm.Models.Profile;

/// <summary>
/// Persisted Auto-Lair "setup" — a named bundle of marked lair rooms
/// the scheduler cycles through. Mirrors <see cref="Game.Map.Loop"/>'s
/// role for loops: storage shape, not runtime state. The
/// <see cref="Game.Map.LairManager"/> round-trips one file per setup
/// under <see cref="Services.AppPaths.GameDataSetLoopsFolder"/> with the
/// <see cref="Game.Map.LairManager.LairFileSuffix"/> filename suffix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why set-scoped, not per-character</b>: lair locations are game-
/// data facts (where the spawn lives), not personal preferences. A
/// "lower-sewers rats" setup is reusable by every character on every
/// BBS that shares the same game-data set without copying. Per-character
/// preference belongs in <see cref="Models.Profile.AutoLairSettings"/>
/// instead.
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

    /// <summary>
    /// Folder this setup lives under inside the BBS Loops directory,
    /// relative to it, using <c>/</c> separators (e.g.
    /// <c>"Sewers/Lower"</c>). Empty = the Loops root. Not serialised —
    /// the on-disk subdirectory is the source of truth;
    /// <see cref="Game.Map.LairManager"/> sets this from the file's
    /// location on load and writes the file into the matching
    /// subdirectory on save.
    /// </summary>
    [JsonIgnore]
    public string Folder { get; set; } = string.Empty;

    public LairSetup() { }

    public LairSetup(string name, IEnumerable<LairMarker> markers)
    {
        Name = name ?? string.Empty;
        Markers = new List<LairMarker>(markers ?? Array.Empty<LairMarker>());
    }

    /// <summary>Marker count — read-only convenience for UI bindings.</summary>
    public int MarkerCount => Markers.Count;
}
