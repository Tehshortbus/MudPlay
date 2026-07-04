using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FujinTerm.Models.Profile;

// Persisted Auto-Lair "setup" — a named bundle of marked lair rooms the
// scheduler cycles through. Mirrors Game.Map.Loop's role for loops: storage
// shape, not runtime state. The Game.Map.LairManager round-trips one file per
// setup under Services.AppPaths.GameDataSetLoopsFolder with the
// Game.Map.LairManager.LairFileSuffix filename suffix.
//
// Why set-scoped, not per-character: lair locations are game-data facts (where
// the spawn lives), not personal preferences. A "lower-sewers rats" setup is
// reusable by every character on every BBS that shares the same game-data set
// without copying. Per-character preference belongs in AutoLairSettings instead.
//
// Why unordered markers: the scheduler picks the next lair each tick based on
// respawn timers + travel cost. The user's "add" order doesn't constrain
// execution order, and re-ordering markers would be misleading UI.
public sealed class LairSetup
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public List<LairMarker> Markers { get; set; } = new();

    // Folder this setup lives under inside the BBS Loops directory, relative to
    // it, using / separators (e.g. "Sewers/Lower"). Empty = the Loops root. Not
    // serialised — the on-disk subdirectory is the source of truth;
    // Game.Map.LairManager sets this from the file's location on load and writes
    // the file into the matching subdirectory on save.
    [JsonIgnore]
    public string Folder { get; set; } = string.Empty;

    public LairSetup() { }

    public LairSetup(string name, IEnumerable<LairMarker> markers)
    {
        Name = name ?? string.Empty;
        Markers = new List<LairMarker>(markers ?? Array.Empty<LairMarker>());
    }

    // Marker count — read-only convenience for UI bindings.
    public int MarkerCount => Markers.Count;
}
